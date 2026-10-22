// Other Tools → WAV to LIP: a port of the WAV_to_LIP app (Whisper transcription + FaceFXWrapper).
import { rpc, on, h, clear, toast, fail, confirmBox, droppedPaths, switchEl, icon, needInfo, helpButton } from './lib.js';
import { app, go, refreshState } from './app.js';

let files = []; // kept while the app runs, like the Python app's queue
const logLines = [];
let statusText = '';

export async function wavLipView() {
  const s = await refreshState();
  const st = s.settings;
  const w = structuredClone(st.wavToLip);
  const save = async () => { app.state = await rpc('settings.save', { settings: { wavToLip: w } }); };
  const selected = new Set();

  const view = h('div.view');
  view.append(h('div.view-head', h('h1', 'WAV to LIP Tool'), helpButton('wav-to-lip-tool'), h('span.sub', 'Drop WAV files → Whisper transcription → Skyrim .lip generation. Each .lip is saved next to its WAV.')));
  const tool = h('div.tool');
  view.append(tool);

  const sel = (value, options, onchange) => {
    const el = h('select', options.map(([v, t]) => h('option', { value: v, selected: v === value }, t)));
    el.addEventListener('change', () => onchange(el.value));
    return el;
  };
  const tools = h('div.section', h('div.section-head', h('h2', 'Lip-generation tools'), h('div.spacer'), h('button.btn.sm', { onclick: () => go('settings', { tab: 'general' }) }, 'Change in Global Settings')),
    h('div.section-body',
      h('div.srow.tool-path', h('div.lbl', h('span.row', { style: { gap: '4px' } }, 'FaceFXWrapper.exe', needInfo('facefx'))), h('div.row', h('span.mono.grow.ellipsis', st.faceFxWrapperPath || '— not set —'), h('span.badge.' + (st.tools.faceFx ? 'b-valid' : 'b-broken'), st.tools.faceFx ? '✓' : '✕'))),
      h('div.srow.tool-path', h('div.lbl', h('span.row', { style: { gap: '4px' } }, 'FonixData.cdf', needInfo('fonix'))), h('div.row', h('span.mono.grow.ellipsis', st.fonixDataPath || '— not set —'), h('span.badge.' + (st.tools.fonix ? 'b-valid' : 'b-broken'), st.tools.fonix ? '✓' : '✕'))),
      h('div.srow.tool-path', { title: 'Built-in Whisper only: converts to 16 kHz for transcription.' }, h('div.lbl', 'ffmpeg.exe'), h('div.row', h('span.mono.grow.ellipsis', st.ffmpegPath || 'included with DBVO Studio'), h('span.badge.' + (st.tools.ffmpeg ? 'b-valid' : 'b-broken'), st.tools.ffmpeg ? '✓' : '✕')))));

  const whisperBody = h('div.section-body');
  const renderWhisper = () => clear(whisperBody).append(
    h('div.row.wrap', { style: { padding: '10px 0', gap: '14px' } },
      h('div.tgroup', h('label', 'Engine'), sel(w.engine, [['builtin', 'Built in (Whisper.net)'], ['faster-whisper', 'My faster-whisper (Python)']], async (v) => { w.engine = v; await save(); renderWhisper(); })),
      h('div.tgroup', h('label', 'Model'), sel(w.model, [['large-v3', 'large-v3'], ['medium', 'medium'], ['small', 'small'], ['base', 'base']], async (v) => { w.model = v; await save(); })),
      h('div.tgroup', h('label', 'Language'), sel(w.language, [['auto', 'auto'], ['en', 'en'], ['sv', 'sv'], ['de', 'de'], ['fr', 'fr'], ['es', 'es'], ['it', 'it'], ['pl', 'pl'], ['ru', 'ru']], async (v) => { w.language = v; await save(); })),
      h('div.tgroup', h('label', 'Device'), sel(w.device, w.engine === 'builtin' ? [['cuda', 'GPU (Vulkan)'], ['cpu', 'CPU']] : [['cuda', 'cuda'], ['cpu', 'cpu']], async (v) => { w.device = v; await save(); })),
      h('div.tgroup', h('label', 'Compute'), sel(w.compute, [['float16', 'float16'], ['int8_float16', 'int8_float16'], ['int8', 'int8']], async (v) => { w.compute = v; await save(); }))),
    h('div.col', { style: { paddingBottom: '10px' } },
      switchEl(w.cpuFallback, async (v) => { w.cpuFallback = v; await save(); }, w.engine === 'builtin' ? 'If GPU initialization fails, retry transcription on CPU' : 'If CUDA initialization fails, retry transcription on CPU/int8'),
      h('div.faint', w.engine === 'builtin'
        ? 'Default: large-v3, GPU, float16 (unquantized). int8 uses the smaller q8_0 model. The first run downloads the model.'
        : `Default matches the WAV_to_LIP app: faster-whisper 1.2.1, large-v3, CUDA, float16. Python: ${w.pythonPath || '— not set (Global Settings → WAV to LIP) —'}`)));
  renderWhisper();
  const whisper = h('div.section', h('div.section-head', h('h2', 'Whisper transcription')), whisperBody);

  const list = h('div.filelist');
  const counter = h('span.dim');
  const renderList = () => {
    clear(list);
    if (!files.length) list.append(h('div.faint', { style: { padding: '30px', textAlign: 'center' } }, 'Drop WAV files or folders here. Folders are scanned recursively.'));
    files.forEach((f, i) => {
      const row = h('div.f' + (selected.has(i) ? '.sel' : ''), { onclick: (e) => { if (!e.ctrlKey) selected.clear(); selected.has(i) ? selected.delete(i) : selected.add(i); renderList(); } },
        h('span.mono.grow.ellipsis', { title: f.path }, f.path.split(/[\\/]/).pop()), f.hasLip ? h('span.badge.b-skip', 'has .lip') : null,
        h('button.act-btn', { title: 'Open the folder: ' + f.path.replace(/[\\/][^\\/]*$/, ''), onclick: (e) => { e.stopPropagation(); rpc('open.path', { path: f.path }).catch(fail); } }, icon('folder', { size: 14 })));
      list.append(row);
    });
    counter.textContent = `${files.length} WAV file(s) queued.`;
  };
  const addPaths = async (paths) => {
    if (!paths || !paths.length) return;
    const found = await rpc('wavlip.scan', { paths });
    const known = new Set(files.map((f) => f.path.toLowerCase()));
    for (const f of found) if (!known.has(f.path.toLowerCase())) { files.push(f); known.add(f.path.toLowerCase()); }
    renderList();
  };
  list.addEventListener('dragover', (e) => { e.preventDefault(); list.classList.add('over'); });
  list.addEventListener('dragleave', () => list.classList.remove('over'));
  list.addEventListener('drop', async (e) => {
    e.preventDefault();
    e.stopPropagation();
    try { await addPaths(await droppedPaths([...(e.dataTransfer.files || [])])); } catch (err) { fail(err); }
  });
  renderList();

  const statusEl = h('div.dim', statusText || `Tools root: ${st.faceFxWrapperPath ? st.faceFxWrapperPath.replace(/\\[^\\]*$/, '') : '—'}`);
  const bar = h('div.bar', h('div.bar-fill', { style: { width: '0%' } }));
  const logEl = h('div.log', logLines.join('\n'));
  const log = (t) => { logLines.push(t); if (logLines.length > 2000) logLines.shift(); logEl.textContent = logLines.join('\n'); logEl.scrollTop = logEl.scrollHeight; };
  const startBtn = h('button.btn.primary', { title: 'Transcribe each WAV and write a .lip next to it', disabled: !!app.job, onclick: start }, 'Transcribe + Generate .LIP');

  whisper.style.maxWidth = 'none';
  tools.style.maxWidth = 'none';
  tools.classList.add('compact');
  tool.append(h('div.tool-top', whisper, tools),
    h('div.tool-bottom', h('div.section', { style: { maxWidth: 'none' } }, h('div.section-head', h('h2', 'WAV files'), counter), h('div.section-body', { style: { padding: '10px 16px' } }, list,
      h('div.row', { style: { marginTop: '10px' } },
        h('button.btn', { onclick: async () => addPaths(await rpc('pick.files', { title: 'Select WAV files', filter: 'WAV audio|*.wav' })) }, 'Add WAV files…'),
        h('button.btn', { onclick: async () => { const f = await rpc('pick.folder', { title: 'Select folder containing WAV files' }); if (f) addPaths([f]); } }, 'Add folder…'),
        h('button.btn', { title: 'Remove the selected files from the list (not from disk)', onclick: () => { files = files.filter((_, i) => !selected.has(i)); selected.clear(); renderList(); } }, 'Remove selected'),
        h('button.btn.ghost', { title: 'Empty the list (files stay on disk)', onclick: () => { files = []; selected.clear(); renderList(); } }, 'Clear'),
        h('div.spacer'), startBtn))),
    h('div.tool-log', bar, statusEl, logEl)));

  async function start() {
    if (app.job) return;
    if (!files.length) { toast('Add at least one WAV file first.', true); return; }
    const overwrites = files.filter((f) => f.hasLip).length;
    if (overwrites && !(await confirmBox('Overwrite .lip files?', `${overwrites} .lip file(s) already exist and would be overwritten. Continue?`, 'Continue'))) return;
    try {
      startBtn.disabled = true;
      statusEl.textContent = 'Starting Whisper. The first run may download the model.';
      await rpc('wavlip.run', { files: files.map((f) => f.path) });
    } catch (e) { startBtn.disabled = false; fail(e); }
  }

  on('wavlip', (e) => {
    if (!document.body.contains(view)) {
      if (e.kind === 'log' || e.kind === 'done' || e.kind === 'fatal') logLines.push(e.text);
      return;
    }
    if (e.kind === 'log') log(e.text);
    else if (e.kind === 'status') {
      statusText = e.text;
      statusEl.textContent = e.text;
      if (e.total) bar.firstChild.style.width = (100 * e.index / e.total) + '%';
    } else if (e.kind === 'done' || e.kind === 'fatal') {
      log(e.text);
      statusText = e.kind === 'fatal' ? 'Stopped due to an error.' : e.text;
      statusEl.textContent = statusText;
      bar.firstChild.style.width = e.kind === 'done' ? '100%' : '0%';
      startBtn.disabled = false;
      rpc('wavlip.scan', { paths: files.map((f) => f.path) }).then((r) => { files = r; renderList(); }).catch(() => {});
    }
  });
  return view;
}
