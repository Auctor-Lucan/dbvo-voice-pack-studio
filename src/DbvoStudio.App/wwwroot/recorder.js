// Record a take with the microphone: record, listen, trim, then keep it as it is or convert it into the voice pack's
// voice with the Voice Changer (ElevenLabs speech-to-speech), when that is set up.
import { rpc, h, clear, toast, fail, modal } from './lib.js';

const fmt = (s) => (Math.max(0, s) || 0).toFixed(2) + ' s';

/** Opens the recorder for one line. Resolves to the updated line (or null when nothing was kept). */
export async function recordTake({ patchId, line, onSaved, kind = 'patch' }) {
  const lineId = line.lineId || line.id;
  const target = await rpc(kind + '.recordTarget', { id: patchId, lineId }).catch(() => ({ ready: false, why: '' }));
  let stream = null, rec = null, chunks = [], blob = null, url = null, startT = 0, raf = 0, timer = 0, saved = null;
  const audio = new Audio();
  const meter = h('div.rec-meter', h('i'));
  const clock = h('span.rec-clock', '0.00 s');
  const recBtn = h('button.btn.primary.rec-btn', { title: 'Start recording (the line stays on screen to read from)' }, '● Record');
  const status = h('div.faint', 'Press Record and read the line. Speak close to the microphone; a quiet room helps.');
  const startIn = h('input', { type: 'range', min: 0, max: 0, step: 0.01, value: 0 });
  const endIn = h('input', { type: 'range', min: 0, max: 0, step: 0.01, value: 0 });
  const startLbl = h('span.mono', '0.00 s'), endLbl = h('span.mono', '0.00 s');
  const trim = h('div.rec-trim.hidden',
    h('div.row', h('span.faint', 'Start'), startIn, startLbl),
    h('div.row', h('span.faint', 'End'), endIn, endLbl),
    h('div.row', h('button.btn.sm', { title: 'Play from Start to End', onclick: () => playSel() }, '▶ Play selection'), h('button.btn.sm.ghost', { title: 'Back to the whole recording', onclick: () => { startIn.value = 0; endIn.value = endIn.max; syncLbl(); } }, 'Reset trim')));
  const keepBtn = h('button.btn', { disabled: true, title: kind === 'grunts' ? 'Keep your own voice as a new line' : 'Keep your own voice as a new take (a .lip is made from the line)' }, 'Keep my recording');
  const convBtn = h('button.btn.primary', { disabled: true, title: target.ready ? `Convert the recording into ${target.voiceName} with the Voice Changer (ElevenLabs speech-to-speech), then keep it as a new take. Billed by audio length.` : target.why || 'The Voice Changer is not set up.' },
    `Convert to ${target.voiceName || 'the voice pack voice'} and keep`);
  const actions = h('div.row', { style: { flexWrap: 'wrap' } }, keepBtn, convBtn, h('div.spacer'), h('button.btn.ghost', { title: 'Throw this recording away', onclick: () => discard() }, 'Discard'));

  const syncLbl = () => {
    if (+startIn.value > +endIn.value - 0.05) startIn.value = Math.max(0, +endIn.value - 0.05);
    startLbl.textContent = fmt(+startIn.value);
    endLbl.textContent = fmt(+endIn.value);
  };
  startIn.addEventListener('input', syncLbl);
  endIn.addEventListener('input', syncLbl);

  function playSel() {
    if (!url) return;
    audio.src = url;
    audio.currentTime = +startIn.value;
    const stopAt = +endIn.value;
    const tick = () => { if (audio.currentTime >= stopAt) { audio.pause(); audio.removeEventListener('timeupdate', tick); } };
    audio.addEventListener('timeupdate', tick);
    audio.play().catch(fail);
  }

  function discard() {
    blob = null; if (url) URL.revokeObjectURL(url); url = null;
    trim.classList.add('hidden'); keepBtn.disabled = true; convBtn.disabled = true;
    clock.textContent = '0.00 s';
    status.textContent = 'Recording discarded. Press Record to try again.';
  }

  // The microphone opens when the recorder opens, and the meter runs from then on. A microphone that has only just been
  // switched on often starts with a burst of static or a click; opening it early lets that pass before Record is pressed.
  let ctx = null, opened = 0;
  async function openMic() {
    if (stream) return true;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 1 } });
    } catch (e) {
      status.textContent = /denied|permission/i.test(String(e && (e.name + e.message)))
        ? 'Windows or the app was not allowed to use the microphone. Allow it in Windows Settings → Privacy & security → Microphone, then try again.'
        : 'No microphone was found. Plug one in or pick one in Windows sound settings, then press Record again.';
      return false;
    }
    opened = performance.now();
    ctx = new AudioContext();
    const an = ctx.createAnalyser(); an.fftSize = 1024;
    ctx.createMediaStreamSource(stream).connect(an);
    const buf = new Float32Array(an.fftSize);
    const bar = meter.firstChild;
    const draw = () => {
      an.getFloatTimeDomainData(buf);
      let peak = 0; for (const x of buf) peak = Math.max(peak, Math.abs(x));
      bar.style.width = Math.min(100, peak * 100) + '%';
      bar.classList.toggle('hot', peak > 0.97);
      raf = requestAnimationFrame(draw);
    };
    draw();
    return true;
  }

  async function startRec() {
    if (!(await openMic())) return;
    if (ctx && ctx.state === 'suspended') ctx.resume().catch(() => {});
    discard();
    const settle = 600 - (performance.now() - opened); // give a just-opened microphone a moment
    if (settle > 0) { status.textContent = 'Getting the microphone ready…'; recBtn.disabled = true; await new Promise((r) => setTimeout(r, settle)); recBtn.disabled = false; }
    const type = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus'].find((t) => MediaRecorder.isTypeSupported(t)) || '';
    rec = new MediaRecorder(stream, type ? { mimeType: type, audioBitsPerSecond: 256000 } : undefined);
    chunks = [];
    rec.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
    rec.onstop = () => {
      blob = new Blob(chunks, { type: rec.mimeType || 'audio/webm' });
      url = URL.createObjectURL(blob);
      const dur = (performance.now() - startT) / 1000;
      startIn.max = endIn.max = dur.toFixed(2); startIn.value = 0; endIn.value = dur.toFixed(2); syncLbl();
      trim.classList.remove('hidden');
      keepBtn.disabled = false; convBtn.disabled = !target.ready;
      status.textContent = 'Listen, trim the start and end if needed, then keep it.';
      audio.src = url; audio.play().catch(() => {});
    };
    rec.start(200);
    startT = performance.now();
    timer = setInterval(() => { clock.textContent = fmt((performance.now() - startT) / 1000); }, 50);
    recBtn.textContent = '■ Stop';
    recBtn.title = 'Stop recording';
    recBtn.classList.add('danger');
    status.textContent = 'Recording… read the line, then press Stop.';
    recBtn.onclick = stopRec;
  }
  function stopRec() {
    if (rec && rec.state !== 'inactive') rec.stop();
    clearInterval(timer);
    recBtn.textContent = '● Record again';
    recBtn.title = 'Record the line again (replaces this recording)';
    recBtn.classList.remove('danger');
    recBtn.onclick = startRec;
  }
  recBtn.onclick = startRec;

  async function keep(convert) {
    if (!blob) return;
    keepBtn.disabled = convBtn.disabled = recBtn.disabled = true;
    status.textContent = convert ? `Converting to ${target.voiceName}${kind === 'grunts' ? '' : ' and making the .lip'}…` : kind === 'grunts' ? 'Saving…' : 'Saving and making the .lip…';
    try {
      const data = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(String(r.result).split(',')[1]); r.onerror = rej; r.readAsDataURL(blob); });
      const ext = (blob.type || '').includes('ogg') ? '.ogg' : '.webm';
      const r = await rpc(kind + '.record', { id: patchId, lineId, data, ext, start: +startIn.value, end: +endIn.value, convert });
      saved = r.line;
      if (onSaved) onSaved(r.line, r.takeId);
      toast(convert ? 'Converted and kept as a new take.' : kind === 'grunts' ? 'Recording added.' : 'Recording kept as a new take.');
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })); // closes the recorder
    } catch (e) {
      fail(e);
      status.textContent = 'Not saved: ' + (e.message || e);
      keepBtn.disabled = false; convBtn.disabled = !target.ready; recBtn.disabled = false;
    }
  }
  openMic(); // start the microphone (and its meter) now, so it has settled before Record
  keepBtn.onclick = () => keep(false);
  convBtn.onclick = () => keep(true);

  await modal('Record this line', [
    h('div.box.rec-line', line.spoken || line.finalLine || line.text),
    h('div.row', recBtn, meter, clock),
    status, trim,
    target.ready ? h('div.faint', `Voice Changer: ${target.voiceName}. Your delivery and timing are kept; only the voice changes.`) : h('div.faint', target.why),
    actions,
  ], [{ label: 'Close', value: null }], { help: 'recording-a-take' });
  // closed: release the microphone
  if (rec && rec.state !== 'inactive') { try { rec.stop(); } catch { /* already stopped */ } }
  clearInterval(timer); cancelAnimationFrame(raf);
  if (ctx) ctx.close().catch(() => {});
  if (stream) stream.getTracks().forEach((t) => t.stop());
  audio.pause();
  if (url) URL.revokeObjectURL(url);
  return saved;
}
