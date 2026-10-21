// Global Settings: tools, voice packs, pronunciation, omit references, categories, library. Saved as you change them.
import { VOICE_COLORS, voiceChip } from './phase2.js';
import { THEMES, applyTheme } from './themes.js';
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, switchEl, segmented, popupMenu, needInfo } from './lib.js';
import { app, go, refreshState } from './app.js';

const TABS = [
  ['general', 'General & tools'],
  ['voices', 'Voice packs'],
  ['pronunciation', 'Pronunciation'],
  ['references', 'Omit references'],
  ['categories', 'Categories'],
  ['credits', 'Credits'],
  ['library', 'Library'],
];

function section(title, extra, ...rows) {
  return h('div.section', h('div.section-head', h('h2', title), extra), h('div.section-body', rows));
}
function srow(label, help, control) {
  return h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', control));
}
function status(ok, label) {
  if (label === 'found') label = ok ? 'found' : 'not found';
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
const link = (url, text) => h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url }); } }, text || url);

export async function settingsView(tab, selectedId) {
  await refreshState();
  tab = TABS.some(([k]) => k === tab) ? tab : store.get('settingsTab', 'general');
  const view = h('div.view');
  const sub = h('div.subnav');
  const body = h('div.view-body');
  view.append(h('div.view-head', h('h1', 'Global Settings'), h('span.sub', 'Everything here is saved as you change it.')), h('div.settings', sub, body));

  let voiceSel = selectedId;
  const show = async (t, id) => {
    tab = t;
    if (id !== undefined) voiceSel = id;
    store.set('settingsTab', t);
    renderSub();
    clear(body);
    const render = { general, voices, pronunciation, references, categories, library, credits }[tab];
    try { body.append(await render(voiceSel, () => show(tab), (vid) => { voiceSel = vid; renderSub(); })); } catch (e) { fail(e); }
  };
  function renderSub() {
    clear(sub);
    for (const [k, label] of TABS) {
      sub.append(h('button.nav-item' + (k === tab ? '.active' : ''), { onclick: () => show(k) }, label));
      if (k === 'voices') {
        // The voice pack list lives under its menu item, so the editor gets the full width.
        sub.append(h('div', { style: { padding: '2px 0 8px 12px', display: 'flex', flexDirection: 'column', gap: '2px' } },
          h('button.btn.primary.sm', { style: { justifyContent: 'center', margin: '4px 0 6px' }, onclick: () => show('voices', 'new') }, '+ New voice pack'),
          app.state.voicePacks.map((v) => h('button.nav-item' + (tab === 'voices' && voiceSel === v.id ? '.active' : ''), { style: { padding: '5px 10px' }, onclick: () => show('voices', v.id) },
            h('span', { style: { width: '8px', height: '8px', borderRadius: '50%', background: v.color || '#58a6ff', flexShrink: 0 } }),
            h('div', { style: { minWidth: 0 } }, h('div.ellipsis', v.name), h('div.faint', { style: { fontSize: '11px' } }, (v.packId || 'no pack ID') + ' · ' + (v.naming === 'Legacy' ? 'Legacy' : 'DBVO 2.0')))))));
      }
    }
  }
  if (tab === 'voices' && !voiceSel) voiceSel = (app.state.voicePacks[0] || {}).id || 'new';
  await show(tab, voiceSel);
  return view;
}

async function credits() {
  const u = await rpc('reference.info', {}).catch(() => null);
  const day = (t) => (t ? new Date(t).toLocaleDateString() : '?');
  return section('Credits', null,
    h('div.srow', h('div.lbl', 'UESP', h('small', 'The Unofficial Elder Scrolls Pages')),
      h('div.col', { style: { gap: '6px' } },
        h('div', 'NPC voice types, quoted dialogue and book authors used by NPC Revoice come from ', link('https://en.uesp.net/', 'UESP — en.uesp.net'),
          '. All credit for this information goes to the UESP editors and community; it was collected by Auctor Lucan.'),
        u ? h('div.faint', `${u.pages.toLocaleString()} Skyrim pages (revisions ${day(u.revisionsFrom)} – ${day(u.revisionsTo)}) → ${u.npcs.toLocaleString()} NPCs, ${u.quotes.toLocaleString()} quotes, ${u.books.toLocaleString()} books.`) : h('div.faint', 'Reference data not found in this build.'),
        h('div.faint', 'UESP text is licensed under ', link('https://creativecommons.org/licenses/by-sa/2.5/', 'CC BY-SA 2.5'),
          '; the derived data in this app is shared under the same license. Each entry links back to its UESP article (', link('https://en.uesp.net/wiki/UESPWiki:Copyrights', 'UESP copyright policy'), ').'),
        h('div.faint', 'Used for base-game lines only: when several NPCs share a voice type, a UESP quote of the exact line by one of them picks the speaker (shown as "UESP quote", certainty Probable).'))));
}

async function saveSettings(patchObj) {
  app.state = await rpc('settings.save', { settings: patchObj });
  return app.state;
}

// ------------------------------------------------------------------ general & tools

const AI_PRESETS = [
  { id: 'ollama', label: 'Ollama (free, on this PC)', endpoint: 'http://localhost:11434/v1', model: 'llama3.1', key: false,
    steps: [['Install Ollama from ', 'https://ollama.com/download'], ['Open a terminal and run: ollama pull llama3.1 (about 5 GB; qwen2.5 or mistral also work)'], ['Leave Ollama running; no key is needed.']] },
  { id: 'lmstudio', label: 'LM Studio (free, on this PC)', endpoint: 'http://localhost:1234/v1', model: '', key: false,
    steps: [['Install LM Studio from ', 'https://lmstudio.ai'], ['Download a chat model in LM Studio (e.g. Llama 3.1 8B Instruct)'], ['Developer tab → Start Server. Put the model name shown there in Model.']] },
  { id: 'openai', label: 'OpenAI (paid, online)', endpoint: 'https://api.openai.com/v1', model: 'gpt-4o-mini', key: true,
    steps: [['Create an API key at ', 'https://platform.openai.com/api-keys'], ['Add a little credit under Billing; tagging a line costs a fraction of a cent.'], ['Paste the key below and Save key.']] },
  { id: 'anthropic', label: 'Anthropic Claude (paid, online)', endpoint: 'https://api.anthropic.com/v1/', model: 'claude-haiku-5-5', key: true,
    steps: [['Create an API key at ', 'https://console.anthropic.com/settings/keys'], ['Uses Anthropic\'s OpenAI-compatible endpoint. Use a current model name from Anthropic\'s docs.'], ['Paste the key below and Save key.']] },
];

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
  const textInput = (value, onSave, placeholder = '') => { const i = h('input', { type: 'text', value: value || '', placeholder, style: { width: '100%' } }); i.addEventListener('change', () => onSave(i.value.trim())); return i; };

  // AI presets
  const aiKey = h('input', { type: 'password', placeholder: st.aiProvider.hasKey ? '•••••••• (saved)' : 'not needed for Ollama / LM Studio', style: { width: '260px' } });
  const aiHelp = h('div');
  const aiResult = h('span.dim');
  const presetOf = () => AI_PRESETS.find((p) => st.aiProvider.endpoint && st.aiProvider.endpoint.replace(/\/$/, '') === p.endpoint.replace(/\/$/, ''));
  const renderAiHelp = (p) => clear(aiHelp).append(p ? h('div.help.steps-help', h('b', p.label + ': '), h('ol', p.steps.map(([a, url]) => h('li', a, url ? link(url) : null)))) : null);
  renderAiHelp(presetOf());
  const presetSel = h('select', h('option', { value: '' }, 'Custom'), AI_PRESETS.map((p) => h('option', { value: p.id, selected: presetOf() === p }, p.label)));
  presetSel.addEventListener('change', async () => {
    const p = AI_PRESETS.find((x) => x.id === presetSel.value);
    if (!p) return;
    await saveSettings({ aiEndpoint: p.endpoint, aiModel: p.model || st.aiProvider.model });
    rerender();
  });

  // WAV to LIP engine
  const w = structuredClone(st.wavToLip);
  const saveW = async () => { await saveSettings({ wavToLip: w }); };
  const wlBox = h('div');
  const renderWl = () => clear(wlBox).append(
    srow('Engine', 'Built in needs nothing else. faster-whisper uses your own Python setup (e.g. the WAV_to_LIP app) and its CUDA libraries, for identical results to that app.',
      segmented([['builtin', 'Built in (Whisper.net)'], ['faster-whisper', 'My faster-whisper (Python)']], w.engine, async (x) => { w.engine = x; await saveW(); renderWl(); })),
    w.engine === 'builtin'
      ? h('div.help.steps-help', h('b', 'Built-in Whisper: '), h('ol',
        h('li', 'Nothing to install. The first run downloads the model you pick (large-v3 is about 3 GB) into the folder below.'),
        h('li', 'Device "GPU" uses your graphics card through Vulkan (NVIDIA or AMD, no CUDA install needed); if that fails it retries on the CPU.'),
        h('li', 'Needs the Microsoft Visual C++ 2015–2022 Redistributable (x64), which most PCs already have: ', link('https://aka.ms/vs/17/release/vc_redist.x64.exe', 'download')),
        h('li', 'Want CUDA exactly like your Python app? Switch the engine to "My faster-whisper".')))
      : h('div.help.steps-help', h('b', 'Your faster-whisper: '), h('ol',
        h('li', 'Point "Python" at the python.exe of the environment that has faster-whisper installed. For the WAV_to_LIP app that is ', h('span.mono', 'WAV_to_LIP_App\\.venv\\Scripts\\python.exe'), '.'),
        h('li', 'Point "CUDA libraries" at the folder with cuBLAS / cuDNN DLLs (', h('span.mono', 'WAV_to_LIP_App\\cuda_libs'), '); it is put on PATH like run.bat does.'),
        h('li', 'No setup yet? Install Python 3.9+, then: ', h('span.mono', 'py -m venv .venv'), ' and ', h('span.mono', '.venv\\Scripts\\pip install faster-whisper==1.2.1'), '.'),
        h('li', h('button.btn.sm', { title: 'Look for the Python and CUDA folders of your WAV_to_LIP install', onclick: async () => { const r = await rpc('wavlip.detect'); if (r.python) { w.pythonPath = r.python; w.cudaLibsFolder = r.cuda; await saveW(); renderWl(); toast('Found your WAV_to_LIP app.'); } else toast('Not found in Documents\\DBVO Programs\\WAV_to_LIP_App. Pick python.exe yourself.', true); } }, 'Find my WAV_to_LIP app')))),
    w.engine === 'faster-whisper' ? srow('Python', 'python.exe with faster-whisper installed.', h('div.col', pathField(w.pythonPath, async (x) => { w.pythonPath = x; await saveW(); }, { filter: 'python.exe|python.exe;python*.exe', title: 'python.exe' }), status(t.python, 'found'))) : null,
    w.engine === 'faster-whisper' ? srow('CUDA libraries', 'Optional. Folder with cublas64_12.dll / cudnn DLLs.', pathField(w.cudaLibsFolder, async (x) => { w.cudaLibsFolder = x; await saveW(); }, { folder: true, title: 'CUDA libraries folder' })) : null,
    w.engine === 'builtin' ? srow('Model folder', 'Where downloaded Whisper models are kept. Empty = inside the library.', pathField(w.modelsFolder, async (x) => { w.modelsFolder = x; await saveW(); }, { folder: true, title: 'Whisper models folder' })) : null);
  renderWl();

  // Local engines
  const engStatus = { voxcpm: h('span.badge.b-skip', '…'), omnivoice: h('span.badge.b-skip', '…') };
  const checkEngine = async (e) => {
    engStatus[e].textContent = 'checking…';
    try {
      const r = await rpc('engine.health', { engine: e });
      engStatus[e].className = 'badge ' + (r.ok ? 'b-valid' : 'b-broken');
      engStatus[e].textContent = r.ok ? `ready${r.device ? ' · ' + r.device : ''}` : r.phase;
    } catch (err) { engStatus[e].className = 'badge b-broken'; engStatus[e].textContent = 'offline'; }
  };
  checkEngine('voxcpm');
  checkEngine('omnivoice');

  return h('div',
    section('ElevenLabs', null,
      srow(h('span.row', { style: { gap: '4px' } }, 'API key', needInfo('elevenlabs')), 'Stored encrypted for your Windows user. Click ⓘ for where to get one.', h('div.col',
        h('div.row', apiKey,
          h('button.btn.sm.primary', { title: 'Save the key encrypted for your Windows user', onclick: async () => { try { await rpc('settings.setApiKey', { key: apiKey.value.trim() }); apiKey.value = ''; await refreshState(); rerender(); } catch (e) { fail(e); } } }, 'Save key'),
          h('button.btn.sm', { title: 'Check the key and show your remaining credits', onclick: test }, 'Test')),
        apiStatus))),
    section('Appearance', null,
      srow('Colour theme', 'Changes the whole app straight away. Voice pack labels keep their own colours.', h('div.theme-picks', THEMES.map((t) => {
        const cur = ((st.ui || {}).theme || 'github-dark') === t.id;
        const v = t.v;
        return h('button.theme-pick' + (cur ? '.on' : ''), { title: `Use the ${t.name} theme`, style: { background: v.bg, color: v.text, borderColor: cur ? v.accent : v.line },
          onclick: async () => { applyTheme(t.id); await saveSettings({ ui: { theme: t.id } }); rerender(); } },
          h('div.tp-bar', { style: { background: v.deep } }, h('span', { style: { background: v.accent } }), h('span', { style: { background: v.panel } })),
          h('div.tp-body', h('span', { style: { background: v.raised, borderColor: v.line } }), h('span', { style: { background: v.ok } }), h('span', { style: { background: v.warn } }), h('span', { style: { background: v.bad } })),
          h('div.tp-name', t.name + (t.id === 'github-dark' ? ' (default)' : '')));
      })))),
    section('Phase 1', null,
      srow('Voice player-name lines', 'Off (default): lines with <Alias=Player> are left out of the topic files (♪ off), so they are not generated by accident when you voice them in a dedicated Player Names patch. They stay listed under "Player names" in Phase 1 and can still be exported there. On: they are voiced in Phase 2 like any other line. A ♪ you set by hand on a line always wins.',
        switchEl(!!st.playerNameLinesVoiced, (x) => save({ playerNameLinesVoiced: x }), null, { title: 'Voice <Alias=Player> lines in Phase 2' }))),
    section('Generation', null,
      srow('Confirm batch generation', 'Shows the count, characters and engine before generating or converting several lines. Single lines never ask.',
        switchEl((st.ui || {}).confirmBatch !== 'false', (x) => save({ ui: { confirmBatch: x ? 'true' : 'false' } }), null, { title: 'Ask before generating more than one line' }))),
    section('Tools', h('div.row', status(t.fonix, 'FonixData'), status(t.xwma, 'xWMAEncode'), status(t.ffmpeg, 'ffmpeg'), status(t.faceFx, 'FaceFXWrapper'), status(t.sevenZip, '7-Zip')),
      srow(h('span.row', { style: { gap: '4px' } }, 'FonixData.cdf', needInfo('fonix')), 'Needed for LIP files. From the Creation Kit: click ⓘ for how to get it.', pathField(st.fonixDataPath, (x) => save({ fonixDataPath: x }), { filter: 'FonixData|*.cdf', title: 'FonixData.cdf' })),
      srow(h('span.row', { style: { gap: '4px' } }, 'xWMAEncode.exe', needInfo('xwma')), 'Needed for .fuz files. From the Creation Kit: click ⓘ for how to get it.', pathField(st.xwmaEncodePath, (x) => save({ xwmaEncodePath: x }), { filter: 'xWMAEncode|*.exe', title: 'xWMAEncode.exe' })),
      srow('ffmpeg.exe', 'Included with DBVO Studio (Tools\\ffmpeg). Change it only to use another copy.', pathField(st.ffmpegPath, (x) => save({ ffmpegPath: x }), { filter: 'ffmpeg|*.exe', title: 'ffmpeg.exe' })),
      srow(h('span.row', { style: { gap: '4px' } }, 'FaceFXWrapper.exe', needInfo('facefx')), 'Needed for LIP files. A free download: click ⓘ for how to get it. Found automatically if DBVO Voice Pack Maker is installed.', pathField(st.faceFxWrapperPath, (x) => save({ faceFxWrapperPath: x }), { filter: 'FaceFXWrapper|*.exe', title: 'FaceFXWrapper.exe' })),
      srow('7z.exe', 'Included with DBVO Studio (Tools\\7-Zip), for .7z archives. Change it only to use another copy.', pathField(st.sevenZipPath, (x) => save({ sevenZipPath: x }), { filter: '7z.exe|7z.exe;7za.exe', title: '7z.exe' }))),
    section('Plugins', null,
      srow('Base game plugins folder', 'A folder with Skyrim.esm, Update.esm and the DLC masters (your game\'s Data folder works). Used as masters when reading plugins.',
        h('div.col', pathField(st.baseGameFolder, (x) => save({ baseGameFolder: x }), { folder: true, title: 'Base game plugins' }), status(t.baseGame, 'found'))),
      srow('Strings folder', 'For localized plugins such as Skyrim.esm: a folder with Skyrim_English.STRINGS and so on. Use the strings your players have.',
        h('div.col', pathField(st.stringsFolder, (x) => save({ stringsFolder: x }), { folder: true, title: 'Strings folder' }), status(t.strings, 'found'))),
      srow('Strings language', 'The language part of the strings file names.', textInput(st.stringsLanguage, (x) => save({ stringsLanguage: x || 'English' })))),
    section('AI auto-tagging', null,
      h('div.help', { style: { margin: '10px 0' } }, 'ElevenLabs has no API for its Enhance button, so "Auto-tag" in Phase 2 asks an AI model you choose to add [tags] without changing any words (suggestions that change words are thrown away). Pick a preset to fill in the fields, follow its steps, then Test.'),
      srow('Preset', null, presetSel),
      aiHelp,
      srow('Endpoint', 'An OpenAI-compatible address (ends in /v1).', textInput(st.aiProvider.endpoint, (x) => save({ aiEndpoint: x }), 'http://localhost:11434/v1')),
      srow('Model', 'The model name exactly as the provider lists it.', textInput(st.aiProvider.model, (x) => save({ aiModel: x }), 'llama3.1, gpt-4o-mini, …')),
      srow('API key', 'Only for online providers. Stored encrypted.',
        h('div.row', aiKey, h('button.btn.sm', { title: 'Save the key encrypted for your Windows user', onclick: async () => { await rpc('settings.setAiKey', { key: aiKey.value.trim() }); aiKey.value = ''; await refreshState(); rerender(); toast('Saved.'); } }, 'Save key'))),
      srow('Test', 'Sends one sample line.', h('div.row', h('button.btn.sm', { title: 'Send one sample line to the AI and show its answer', onclick: async () => {
        aiResult.textContent = 'Asking…';
        try {
          const r = await rpc('ai.test');
          aiResult.textContent = r.ok ? `✓ ${r.ms} ms: ${r.suggestion}` : '✕ ' + (r.error || 'No answer');
        } catch (e) { aiResult.textContent = '✕ ' + e.message; }
      } }, 'Test connection'), aiResult))),
    section('WAV to LIP (Other Tools)', null, wlBox),
    section('Local voice engines', null,
      h('div.help', { style: { margin: '10px 0' } }, 'VoxCPM and OmniVoice run on your own PC, as with DBVO Voice Pack Maker 2.7.0 (same servers and settings). Start the server, then pick the engine for a voice pack (Voice packs → Engine, or the Phase 2 toolbar).'),
      srow('VoxCPM server', 'Default http://localhost:7861', h('div.row', textInput(st.localEngines.voxCpmAddress, (x) => save({ voxCpmAddress: x }), 'http://localhost:7861'), engStatus.voxcpm, h('button.btn.sm', { onclick: () => checkEngine('voxcpm') }, 'Check'))),
      srow('OmniVoice server', 'Default http://localhost:7862', h('div.row', textInput(st.localEngines.omniVoiceAddress, (x) => save({ omniVoiceAddress: x }), 'http://localhost:7862'), engStatus.omnivoice, h('button.btn.sm', { onclick: () => checkEngine('omnivoice') }, 'Check')))),
  );
}

// ------------------------------------------------------------------ voice packs

const NEW_VOICE = () => ({
  id: Math.random().toString(36).slice(2, 10), name: '', packId: '', author: '', naming: 'Legacy', engine: 'elevenlabs', color: '#58a6ff',
  eleven: { voiceId: '', voiceName: '', modelId: 'eleven_v4', stability: 0.5, similarity: 0.75, style: 0, speakerBoost: false, speed: 1, seed: null, outputFormat: 'mp3_44100_128', languageCode: '', textNormalization: 'auto', concurrency: 5, pronunciationDictionaryIds: [] },
  voxCpm: { voice: '', cfgValue: 2.1, inferenceTimesteps: 12, normalize: true, retryBadcase: true, referenceMode: false, controlCfgValue: 2.7, language: 'en', concurrency: 1 },
  omniVoice: { voice: '', model: 'k2-fsa/OmniVoice', numStep: 32, speed: 1, guidanceScale: 3, concurrency: 1 },
  sts: { voiceId: '', voiceName: '', modelId: 'eleven_multilingual_sts_v2', stability: 0.5, similarity: 0.75, style: 0, speakerBoost: false, seed: null, removeBackgroundNoise: false, outputFormat: 'mp3_44100_128', concurrency: 5 },
  tags: ['[whispers]', '[sighs]', '[laughs]', '[sarcastic]', '[curious]', '[excited]'],
  pronunciations: [], destinations: { wavLipFolder: '', finalRoot: '', fixFolder: '', fuzTemplate: 'Sound\\DBVO\\{packId}', releaseFolder: '' },
  defaultReferenceIds: [], settingsLocked: false, loudnorm: false, gainDb: 0, aliasParsing: false, overwriteWavLip: false, overwriteFuz: false, trimTailMs: 15, fadeOutMs: 10,
});

async function voices(selectedId, rerender, onSelect) {
  const packs = app.state.voicePacks;
  const editor = h('div');
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
      const isNew = i < 0;
      if (isNew) packs.push(saved); else packs[i] = saved;
      if (current && current.id === saved.id) { savedEl.textContent = 'Saved ✓'; savedEl.className = 'saved ok'; }
      if (onSelect) onSelect(isNew ? saved.id : (current ? current.id : saved.id)); // refresh the name / colour in the left list
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

  function txt(obj, key, opts = {}) {
    const i = h('input', { type: opts.type || 'text', value: obj[key] ?? '', placeholder: opts.placeholder || '', style: { width: opts.width || '100%' } });
    i.addEventListener('input', () => { obj[key] = opts.type === 'number' ? Number(i.value) : i.value; persistSoon(); if (opts.after) opts.after(); });
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
    const preview = h('div.box.mono', { style: { fontSize: '12px', whiteSpace: 'pre-wrap' } });
    const renderPreview = () => {
      const ex = (t) => (t || '').replaceAll('{mod}', 'Lucien').replaceAll('{packId}', v.packId || 'packid').replaceAll('{voicePack}', v.name || 'Voice');
      preview.textContent = [
        'For a mod called "Lucien", a new DBVO Patch gets:',
        `  WAV + LIP folder   ${ex(d.wavLipFolder) || '(not set)'}`,
        `  DBVO Fix folder    ${ex(d.fixFolder) || '(not set)'}`,
        `  Final folder       ${ex(d.finalRoot) || '(not set)'}`,
        `  .fuz files in      ${d.finalRoot ? ex(d.finalRoot) + '\\' : ''}${ex(d.fuzTemplate || 'Sound\\DBVO\\{packId}')}`,
      ].join('\n');
    };
    renderPreview();
    const colorIn = h('div.swatches');
    const renderSwatches = () => clear(colorIn).append(VOICE_COLORS.map(([hex, name]) => {
      const c = voiceChip(name, hex);
      c.title = `${name}: used for this voice pack's label everywhere`;
      if ((v.color || '#58a6ff').toLowerCase() === hex) c.classList.add('on');
      c.addEventListener('click', () => { v.color = hex; persistSoon(); renderSwatches(); });
      return c;
    }));
    renderSwatches();

    clear(editor).append(
      section('Voice pack', h('div.row', h('div.spacer'), savedEl, h('button.btn.primary.sm', { onclick: async () => { if (await persistNow()) toast('Saved.'); } }, 'Save')),
        srow('Name', null, txt(v, 'name', { placeholder: 'e.g. Vampira' })),
        srow('Colour', 'The voice pack label takes this colour everywhere, so you always see which voice you are working with.', colorIn),
        srow('DBVO voice pack ID', 'The folder name under Sound\\DBVO\\ and the voice_pack_id in the DBVO JSON.', txt(v, 'packId', { placeholder: 'e.g. vampira', after: renderPreview })),
        srow('Author', null, txt(v, 'author')),
        srow('File naming', 'Legacy names are readable, so you can see which line each file is. DBVO 2.0 names are hashes of the line, identical to DBVO Voice Pack Maker 2.7.0.',
          segmented([['Legacy', 'Legacy (DBVO 1.0)'], ['Dbvo2Hash', 'DBVO 2.0 hash']], v.naming, (x) => { v.naming = x; persistSoon(); renderEditor(); })),
        srow('Engine', 'ElevenLabs, or a local VoxCPM / OmniVoice server (Global Settings → General & tools). Voice, model and the engine\'s settings are picked in the Phase 2 toolbar and remembered here.',
          h('div.col', segmented([['elevenlabs', 'ElevenLabs'], ['voxcpm', 'VoxCPM'], ['omnivoice', 'OmniVoice']], v.engine || 'elevenlabs', (x) => { v.engine = x; persistSoon(); renderEditor(); }),
            h('span.dim', v.engine === 'voxcpm' ? `VoxCPM · ${v.voxCpm.voice || 'no reference voice yet'}` : v.engine === 'omnivoice' ? `OmniVoice · ${v.omniVoice.voice || 'no reference voice yet'}` : `${v.eleven.voiceName || '— no voice picked yet —'} · ${v.eleven.modelId}`)))),
      section('Defaults for new DBVO Patches', null,
        h('div.help', { style: { margin: '10px 0' } }, 'These are only starting points. Each new DBVO Patch copies them, with ', h('b', '{mod}'), ' replaced by the mod name, and from then on the patch\'s own folders count (Phase 2 → Folders & options).'),
        srow('WAV + LIP folder', 'e.g. C:\\DBVO Dev\\Vampira - {mod}\\RawWav', pathField(d.wavLipFolder, (x) => { d.wavLipFolder = x; persistSoon(); renderPreview(); }, { folder: true, title: 'WAV + LIP folder (then add {mod} where the mod name goes)' })),
        srow('DBVO Fix folder', 'Working copy of the DBVO Fix plugins and topic files, apart from the audio. e.g. C:\\DBVO Dev\\Vampira - {mod}\\Fix', pathField(d.fixFolder, (x) => { d.fixFolder = x; persistSoon(); renderPreview(); }, { folder: true, title: 'DBVO Fix folder (then add {mod})' })),
        srow('Final folder', 'Your FOMOD root, e.g. C:\\DBVO Vampira FOMOD. Phase 3 builds the installer from here.', pathField(d.finalRoot, (x) => { d.finalRoot = x; persistSoon(); renderPreview(); }, { folder: true, title: 'Final folder' })),
        srow('.fuz sub-folder', 'Inside the final folder. e.g. Vamp - {mod}\\Sound\\DBVO\\{packId}; the DBVO Fix plugins go next to that Sound folder.', txt(d, 'fuzTemplate', { placeholder: 'Sound\\DBVO\\{packId}', after: renderPreview })),
        srow('Release folder', 'Where Phase 3 writes the packed FOMOD archive.', pathField(d.releaseFolder, (x) => { d.releaseFolder = x; persistSoon(); }, { folder: true, title: 'Release folder' })),
        srow('Preview', null, preview)),
      section('Tags toolbar', null, srow('Tags for this voice', 'Shown above the Phase 2 grid (ElevenLabs v3 / v4).', tagsBox)),
      section('Voice pack pronunciation', h('div.row', h('span.pill.gold', 'THIS VOICE'), h('div.spacer'), pronFileButtons(v.id, async () => { await refreshState(); const fresh = app.state.voicePacks.find((x) => x.id === v.id); if (fresh) { current = structuredClone(fresh); renderEditor(); } })),
        h('div.help', { style: { margin: '10px 0' } }, 'Words this voice should say differently. The global DBVO dictionary (Global Settings → Pronunciation) applies to every voice pack; when both have the same word, ', h('b', 'this voice pack\'s entry wins'), '.', h('div', { style: { marginTop: '8px' } }, pronHelp())),
        pronTable(v.pronunciations, persistSoon)),
      section('Audio', null,
        srow('Tail trim', 'Removes the end-of-clip artefact before the LIP is made.', h('div.row', txt(v, 'trimTailMs', { type: 'number', width: '80px' }), h('span.dim', 'ms cut, then'), txt(v, 'fadeOutMs', { type: 'number', width: '80px' }), h('span.dim', 'ms fade-out'))),
        srow('Loudness normalization', 'EBU R128, −16 LUFS. Off keeps the original DBVO Voice Pack Maker level.', switchEl(v.loudnorm, (x) => { v.loudnorm = x; persistSoon(); })),
        srow('Gain', null, h('div.row', txt(v, 'gainDb', { type: 'number', width: '80px' }), h('span.dim', 'dB')))),
      section('Behaviour', null,
        srow('Alias parsing', 'DBVO voice pack JSON setting (written in Phase 3). Off writes disable_alias_parsing; rarely needed and buggy in game.', switchEl(v.aliasParsing, (x) => { v.aliasParsing = x; persistSoon(); })),
        srow('Lock engine settings', 'Prevents accidental changes in the Phase 2 toolbar.', switchEl(v.settingsLocked, (x) => { v.settingsLocked = x; persistSoon(); })),
        packs.some((x) => x.id === v.id) ? srow('Delete', null, h('button.btn.danger.sm', { onclick: async () => {
          if (!(await confirmBox('Delete voice pack?', `Delete "${v.name}"? DBVO Patches using it stop working.`, 'Delete', true))) return;
          await rpc('voice.delete', { id: v.id });
          await refreshState();
          go('settings', { tab: 'voices' });
        } }, 'Delete voice pack')) : null),
    );
  }

  const initial = packs.find((v) => v.id === selectedId) || (selectedId === 'new' ? null : packs[0]);
  current = initial ? structuredClone(initial) : NEW_VOICE();
  savedEl.textContent = initial ? 'Saved ✓' : 'New — give it a name';
  savedEl.className = 'saved' + (initial ? ' ok' : '');
  // fill fields older voice packs may not have
  const def = NEW_VOICE();
  for (const k of ['voxCpm', 'omniVoice', 'sts']) current[k] = { ...def[k], ...(current[k] || {}) };
  current.destinations = { ...def.destinations, ...current.destinations };
  renderEditor();
  return editor;
}

/** Import / export buttons for a pronunciation list ("global" or a voice pack id). */
function pronFileButtons(target, after) {
  const imp = h('button.btn.sm', { title: 'Import pronunciations (CSV, PLS or DBVO Voice Pack Maker 2.7.0)' }, 'Import ▾');
  imp.addEventListener('click', () => popupMenu(imp, [
    { label: 'From DBVO Voice Pack Maker 2.7.0…', sub: 'Its settings.json; whole-word, case-sensitive entries', onclick: async () => {
      const found = await rpc('pron.find27');
      let path = found[0];
      if (path) {
        const ok = await confirmBox('2.7.0 settings found', `Import the pronunciation list from:\n${path}`, 'Import');
        if (!ok) path = null;
      }
      if (!path) path = await rpc('pick.file', { title: '2.7.0 settings.json', filter: 'settings.json|*.json' });
      if (path) doImport(path);
    } },
    { label: 'CSV file…', sub: 'word, replacement, type (alias/ipa), whole_word, case_sensitive', onclick: async () => { const p = await rpc('pick.file', { title: 'Pronunciation CSV', filter: 'CSV|*.csv;*.txt' }); if (p) doImport(p); } },
    { label: 'ElevenLabs .pls lexicon…', sub: 'The W3C pronunciation lexicon format', onclick: async () => { const p = await rpc('pick.file', { title: 'Pronunciation lexicon', filter: 'Lexicon|*.pls;*.xml' }); if (p) doImport(p); } },
  ]));
  async function doImport(path) {
    try {
      const r = await rpc('pron.import', { target, path });
      app.state = r.state;
      toast(`${r.added} added, ${r.replaced} replaced.`);
      after();
    } catch (e) { fail(e); }
  }
  const exp = h('button.btn.sm', { title: 'Export pronunciations to CSV or PLS' }, 'Export ▾');
  exp.addEventListener('click', () => popupMenu(exp, [
    { label: 'CSV (Excel)…', onclick: () => doExport('csv') },
    { label: 'ElevenLabs .pls lexicon…', sub: 'Upload on ElevenLabs or share with other tools', onclick: () => doExport('pls') },
  ]));
  async function doExport(ext) {
    const path = await rpc('pick.save', { title: 'Save pronunciation list', filter: ext === 'pls' ? 'Lexicon|*.pls' : 'CSV|*.csv', name: (target === 'global' ? 'DBVO pronunciation' : 'Voice pack pronunciation') + '.' + ext });
    if (!path) return;
    try { const r = await rpc('pron.export', { target, path }); toast(`${r.count} entries saved.`); } catch (e) { fail(e); }
  }
  return h('div.row', imp, exp);
}

/** What the pronunciation columns mean, shown above the global and voice pack dictionaries. */
export function pronHelp() {
  return h('div.col', { style: { gap: '4px' } },
    h('div', h('b', 'Sounds like'), ' replaces the word with a respelling the voice reads as ordinary text, e.g. Alduin → ', h('i', 'Aldooin'), ', Galerion → ', h('i', 'GAL-air-ee-on'), '. Works with every engine (ElevenLabs, VoxCPM, OmniVoice). Use capitals or hyphens to steer the stress.'),
    h('div', h('b', 'IPA'), ' gives the exact sounds in the International Phonetic Alphabet, e.g. ', h('i', 'ˈæl.duː.ɪn'), '. More precise, but only ElevenLabs v3/v4 and Flash v2 read it; other engines ignore the entry. Use it when a respelling keeps coming out wrong.'),
    h('div', h('b', 'Whole word'), ' (on): only the word on its own, not inside a longer word. ', h('b', 'Match case'), ' (on): only with the same capitals, e.g. the region "Reach" but not the verb "reach".'),
    h('div.faint', 'Only the spoken text changes; the subtitles and file names never do.'));
}

export function pronTable(list, onChange) {
  const tbody = h('tbody');
  const render = () => {
    clear(tbody);
    list.forEach((p, i) => {
      const word = h('input', { type: 'text', value: p.word, placeholder: 'Alduin' });
      const rep = h('input', { type: 'text', value: p.replacement, placeholder: p.kind === 'phoneme' ? 'ˈæl.duː.ɪn' : 'Aldooin' });
      const kind = h('select', { title: 'Sounds like: the word is replaced by a respelling the voice reads as normal text (e.g. Aldooin). Works with every engine.\nIPA: an exact pronunciation in the International Phonetic Alphabet (e.g. ˈæl.duː.ɪn). Only ElevenLabs v3/v4 and Flash v2 read it; other engines ignore it.' },
        h('option', { value: 'alias', selected: p.kind === 'alias' }, 'Sounds like'), h('option', { value: 'phoneme', selected: p.kind === 'phoneme' }, 'IPA'));
      const upd = () => { Object.assign(p, { word: word.value, replacement: rep.value, kind: kind.value }); onChange(); };
      [word, rep].forEach((x) => x.addEventListener('input', upd));
      kind.addEventListener('change', upd);
      tbody.append(h('tr', h('td', word), h('td', { style: { width: '130px' } }, kind), h('td', rep),
        h('td', { style: { width: '110px' } }, switchEl(p.wholeWord, (x) => { p.wholeWord = x; onChange(); }, 'whole', { title: 'Whole word: on = only the word on its own ("Alduin" but not "Alduin\'s Bane" glued into another word). Off = also inside longer words.' })),
        h('td', { style: { width: '100px' } }, switchEl(p.caseSensitive, (x) => { p.caseSensitive = x; onChange(); }, 'case', { title: 'Match case: on = only with the same capitals (Reach, not reach). Off = any capitals.' })),
        h('td', { style: { width: '40px' } }, h('button.icon-btn', { title: 'Remove', onclick: () => { list.splice(i, 1); render(); onChange(); } }, '×'))));
    });
  };
  render();
  return h('div', { style: { padding: '6px 0 12px' } },
    h('table.tbl', h('thead', h('tr', h('th', { title: 'The word as it is written in the line' }, 'Word in the line'), h('th', { title: 'Sounds like = respelling; IPA = phonetic alphabet' }, 'Kind'), h('th', 'How it\'s spoken'), h('th', { title: 'Match only the word on its own' }, 'Whole word'), h('th', { title: 'Match only with the same capitals' }, 'Match case'), h('th', ''))), tbody),
    h('div.row', { style: { marginTop: '8px' } },
      h('button.btn.sm', { onclick: () => { list.push({ word: '', kind: 'alias', replacement: '', wholeWord: true, caseSensitive: false }); render(); } }, '+ Add word'),
      h('span.faint', '"Sounds like" works with every engine. IPA is sent as inline /…/ to Eleven v3/v4 and Flash v2. Never changes filenames. Longer words are applied first.')));
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
  return section('DBVO pronunciation dictionary', h('div.row', h('span.pill.blue', 'GLOBAL'), h('div.spacer'), savedEl, pronFileButtons('global', rerender)),
    h('div.help', { style: { margin: '10px 0' } },
      h('p', 'Used by every voice pack: Skyrim names and places once, for all voices. A voice pack can have its own dictionary (Global Settings → Voice packs); ', h('b', 'its entries win'), ' for the same word. Import brings in a 2.7.0 list, a CSV or an ElevenLabs .pls; the same word is replaced, new words are added.'),
      pronHelp()),
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
    h('div.help', { style: { margin: '10px 0' } }, 'Used to sort mods and DBVO Patches, and as the default FOMOD pages in Phase 3. You pick a mod\'s category in Phase 1, or drag a patch to another category. Categories never change folder paths.'),
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
          h('button.btn.sm', { title: 'Open the library folder in Explorer', onclick: () => rpc('library.open') }, 'Open'),
          h('button.btn.sm', { onclick: async () => {
            const folder = await rpc('pick.folder', { title: 'New library location (an empty folder, or an existing DBVO Studio library)' });
            if (!folder) return;
            if (!(await confirmBox('Change library location?', `An empty folder gets a copy of your current library; a folder that already holds a library is used as it is. The app restarts afterwards.\n\n${folder}`, 'Change and restart'))) return;
            try { await rpc('library.change', { path: folder }); } catch (e) { fail(e); }
          } }, 'Change…'))),
      srow('Backups to keep', 'Made automatically before imports, generation, FOMOD builds and deletes.', h('div.row', keep,
        h('button.btn.sm', { title: 'Zip all projects and settings into the backups folder now', onclick: async () => { const p = await rpc('backup.now', { reason: 'manual' }); toast('Backup: ' + p); } }, 'Back up now')))));
}
