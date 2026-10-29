// Edit take: a small waveform editor for one take (cut doubled words, trim, silence, fade, gain).
// The steps are previewed here and sent to the app, which makes the same steps on the unprocessed take, puts its
// post-processing back on and makes a new .lip from the take's text.
import { rpc, h, clear, toast, fail, modal, icon, switchEl, store } from './lib.js';

const XF_MS = 6; // the crossfade at cuts (TakeEdit.CrossfadeMs in the app)

/** Apply one step to an AudioBuffer (the same maths as TakeEdit.Apply). */
function applyOp(ctx, buf, op) {
  const rate = buf.sampleRate, ch = buf.numberOfChannels, frames = buf.length;
  const a = Math.max(0, Math.min(frames, Math.round(Math.min(op.a, op.b) * rate)));
  const b = Math.max(0, Math.min(frames, Math.round(Math.max(op.a, op.b) * rate)));
  const xf = Math.round(XF_MS / 1000 * rate);
  const src = [...Array(ch)].map((_, c) => buf.getChannelData(c));
  const make = (n) => ctx.createBuffer(ch, Math.max(1, n), rate);
  if (op.op === 'delete') {
    if (b <= a) return buf;
    const n = Math.min(xf, a, frames - b);
    const out = make(frames - (b - a) - n);
    for (let c = 0; c < ch; c++) {
      const s = src[c], o = out.getChannelData(c);
      o.set(s.subarray(0, a - n), 0);
      for (let i = 0; i < n; i++) { const g = (i + 0.5) / n; o[a - n + i] = s[a - n + i] * (1 - g) + s[b + i] * g; }
      o.set(s.subarray(b + n), a);
    }
    return out;
  }
  if (op.op === 'keep') {
    if (b <= a) return buf;
    const len = b - a, n = Math.min(xf, Math.floor(len / 2));
    const out = make(len);
    for (let c = 0; c < ch; c++) {
      const o = out.getChannelData(c);
      o.set(src[c].subarray(a, b));
      for (let i = 0; i < n; i++) { const g = (i + 0.5) / n; if (a > 0) o[i] *= g; if (b < frames) o[len - 1 - i] *= g; }
    }
    return out;
  }
  const out = make(frames);
  const lin = Math.pow(10, (op.db || 0) / 20);
  const n = Math.min(xf, Math.floor((b - a) / 2));
  for (let c = 0; c < ch; c++) {
    const s = src[c], o = out.getChannelData(c);
    o.set(s);
    for (let f = a; f < b; f++) {
      let g;
      if (op.op === 'silence') g = f - a < n && a > 0 ? 1 - (f - a + 0.5) / n : b - 1 - f < n && b < frames ? 1 - (b - 1 - f + 0.5) / n : 0;
      else { const p = (f - a + 0.5) / Math.max(1, b - a); g = op.op === 'fadeIn' ? p : op.op === 'fadeOut' ? 1 - p : lin; }
      o[f] = Math.max(-1, Math.min(1, s[f] * g));
    }
  }
  return out;
}

/** Words follow the cuts: a cut word goes, the words after a cut move up. */
function mapWords(words, op) {
  const a = Math.min(op.a, op.b), b = Math.max(op.a, op.b), d = b - a;
  if (op.op === 'delete') return words.filter((w) => !(w.s >= a - 0.02 && w.e <= b + 0.02)).map((w) => w.s >= b - 0.02 ? { ...w, s: w.s - d, e: w.e - d } : w.e <= a + 0.02 ? w : { ...w, e: Math.max(w.s + 0.05, w.e - d) });
  if (op.op === 'keep') return words.filter((w) => w.e > a + 0.02 && w.s < b - 0.02).map((w) => ({ ...w, s: Math.max(0, w.s - a), e: Math.min(d, w.e - a) }));
  return words;
}

const fmt = (t) => t.toFixed(2);

/**
 * Open the editor for a take. kind: patch | revoice | books. onSaved(result) gets what the app sent back
 * (result.result: the line, or for books the book), after a save.
 */
export async function editTake({ kind = 'patch', id, lineId, takeId, onSaved }) {
  let info;
  try { info = await rpc('take.edit.open', { kind, id, lineId, takeId }); } catch (e) { fail(e); return; }
  const ctx = new AudioContext();
  let buf;
  try {
    const res = await fetch(info.url);
    buf = await ctx.decodeAudioData(await res.arrayBuffer());
  } catch (e) { ctx.close(); fail(new Error('This take couldn\'t be read for editing: ' + (e.message || e))); return; }

  let ops = [];
  const history = []; // { buf, ops, words }
  const redo = [];
  let words = null; // [{ w, s, e }] or null while unknown
  let wordsState = info.words ? 'loading' : 'off';
  let sel = null; // { a, b } seconds
  let cursor = 0;
  let zoom = 1;
  let playing = null; // { node, start, from, to }
  let loop = false;
  let snap = store.get('edit:snap', true);
  let closeFn = null;

  const dur = () => buf.duration;
  const css = (n) => getComputedStyle(document.body).getPropertyValue(n).trim() || '#58a6ff';

  // ---------------------------------------------------------------- layout
  const canvas = h('canvas.te-canvas');
  const wordsRow = h('div.te-words');
  const ruler = h('div.te-ruler');
  const inner = h('div.te-inner', canvas, wordsRow, ruler);
  const scroller = h('div.te-scroll', inner);
  const selText = h('span.te-sel');
  const histText = h('span.te-hist.faint');
  const wordsNote = h('span.faint');
  const gainIn = h('input', { type: 'number', value: '3', step: '0.5', style: { width: '64px' }, title: 'dB: + louder, − quieter' });

  const btn = (label, title, fn, cls = '') => h('button.btn.sm' + cls, { title, onclick: fn }, label);
  const bPlay = btn('▶ Play', 'Play from the cursor (Space)', () => togglePlay(false));
  const bPlaySel = btn('▶ Play selection', 'Play the selected part', () => togglePlay(true));
  const bLoop = btn('⟲ Loop', 'Play the selection over and over', () => { loop = !loop; bLoop.classList.toggle('on', loop); });
  const bDel = btn('✂ Delete selection', 'Cut the selected part out (Delete)', () => doOp('delete'), '.danger');
  const bKeep = btn('Keep only selection', 'Cut away everything outside the selection', () => doOp('keep'));
  const bSil = btn('Silence selection', 'Make the selected part silent (the length stays)', () => doOp('silence'));
  const bFin = btn('Fade in', 'Fade in over the selection', () => doOp('fadeIn'));
  const bFout = btn('Fade out', 'Fade out over the selection', () => doOp('fadeOut'));
  const bGain = btn('Gain', 'Make the selection louder or quieter by the dB given', () => doOp('gain', { db: Number(gainIn.value) || 0 }));
  const bUndo = btn('↶ Undo', 'Undo the last edit (Ctrl+Z)', () => undo());
  const bRedo = btn('↷ Redo', 'Redo (Ctrl+Y)', () => doRedo());
  const bNew = h('button.btn.primary', { title: 'Save the edit as a new take of this line (the original take stays; the new one is used)', onclick: () => save('new') }, 'Save as new take');
  const bOver = h('button.btn', { title: 'Replace this take\'s audio and .lip with the edit', onclick: () => save('over') }, 'Save over this take');

  const snapSw = switchEl(snap, (v) => { snap = v; store.set('edit:snap', v); }, 'Snap cuts to quiet points', { title: 'Selection edges move to the quietest point within 25 ms, so a cut lands between sounds' });
  const pp = [info.noise ? 'Noise-reduced pauses' : null, info.effect ? 'Effect: ' + info.effectName : null, info.soften ? 'Soften peaks: ' + (info.soften === 'strong' ? 'Strong' : 'Gentle') : null].filter(Boolean);

  const body = [
    h('div.faint', `Take #${info.slot}${info.used ? ' (in use)' : ''} · ${info.engine || ''} ${info.model || ''} · ${fmt(info.seconds)} s`),
    h('div.te-text', { title: 'The take\'s text: the new .lip is made from it' }, info.text || info.line),
    h('div.row.te-bar', bPlay, bPlaySel, bLoop, h('span.te-sep'),
      btn('− Zoom', 'Zoom out', () => setZoom(zoom / 1.6)), btn('+ Zoom', 'Zoom in (or Ctrl + mouse wheel)', () => setZoom(zoom * 1.6)), btn('Fit', 'Show the whole take', () => setZoom(1)),
      h('div.spacer'), h('span.faint', 'Selection '), selText),
    scroller,
    h('div.row.te-bar', bDel, bKeep, bSil, h('span.te-sep'), bFin, bFout, bGain, gainIn, h('span.faint', 'dB'), h('span.te-sep'), bUndo, bRedo, h('div.spacer'), histText),
    h('div.row.te-box', snapSw,
      h('span.faint', `Cuts get a ${XF_MS} ms crossfade, so they don't click.`), h('div.spacer'), wordsNote),
    h('div.row.te-box', h('b', 'Post-processing'),
      pp.length ? pp.map((x) => h('span.te-chip', x)) : h('span.faint', 'none'),
      h('span.faint', pp.length ? 'You edit the take without it (that\'s what you hear here); it is put back on when you save, as in the grid.' : 'Post-processing added later in the grid goes on the edited take as usual.')),
    h('div.row', h('span.faint', info.lip ? 'A new .lip is made from the take\'s text when you save.' : 'This take has no .lip, so none is made.'), h('div.spacer'), bOver, bNew),
  ];

  // ---------------------------------------------------------------- drawing
  let peaks = null;
  function computePeaks(width) {
    const d = buf.getChannelData(0), n = d.length, per = n / width;
    const mn = new Float32Array(width), mx = new Float32Array(width);
    for (let x = 0; x < width; x++) {
      let lo = 0, hi = 0;
      const s = Math.floor(x * per), e = Math.min(n, Math.floor((x + 1) * per) + 1);
      for (let i = s; i < e; i++) { const v = d[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
      mn[x] = lo; mx[x] = hi;
    }
    peaks = { width, mn, mx };
  }
  const width = () => Math.max(200, Math.round((scroller.clientWidth || 1000) * zoom));
  const tx = (t) => t / dur() * width();
  const xt = (x) => Math.max(0, Math.min(dur(), x / width() * dur()));

  function draw() {
    const W = width(), H = 200, dpr = window.devicePixelRatio || 1;
    inner.style.width = W + 'px';
    canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    if (!peaks || peaks.width !== W) computePeaks(W);
    const g = canvas.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, W, H);
    const acc = css('--accent');
    if (sel) {
      g.fillStyle = 'rgba(56,139,253,.20)';
      g.fillRect(tx(sel.a), 0, tx(sel.b) - tx(sel.a), H);
      g.fillStyle = acc; g.fillRect(Math.round(tx(sel.a)), 0, 1, H); g.fillRect(Math.round(tx(sel.b)), 0, 1, H);
    }
    g.fillStyle = css('--line'); g.fillRect(0, H / 2, W, 1);
    for (let x = 0; x < W; x++) {
      const t = xt(x + 0.5);
      g.fillStyle = sel && t >= sel.a && t <= sel.b ? css('--text') : acc;
      const y1 = H / 2 - peaks.mx[x] * H * 0.48, y2 = H / 2 - peaks.mn[x] * H * 0.48;
      g.fillRect(x, y1, 1, Math.max(1, y2 - y1));
    }
    drawHead();
    // words
    clear(wordsRow);
    if (words) for (const w of words) {
      const el = h('button.te-word', { title: `${fmt(w.s)}–${fmt(w.e)} s · click to select, Shift+click to add to the selection`, onclick: (e) => pickWord(w, e.shiftKey) }, w.w);
      el.style.left = tx((w.s + w.e) / 2) + 'px';
      if (sel && w.s >= sel.a - 0.03 && w.e <= sel.b + 0.03) el.classList.add('in');
      wordsRow.append(el);
    }
    // ruler
    clear(ruler);
    const step = [0.05, 0.1, 0.25, 0.5, 1, 2, 5].find((s) => tx(s) > 70) || 5;
    for (let t = 0; t <= dur() + 1e-6; t += step) ruler.append(Object.assign(h('span', t.toFixed(step < 0.1 ? 2 : step < 1 ? 2 : 1)), { style: `left:${tx(t)}px` }));
    selText.textContent = sel ? `${fmt(sel.a)} – ${fmt(sel.b)} s (${fmt(sel.b - sel.a)} s)` : `cursor ${fmt(cursor)} s · length ${fmt(dur())} s`;
    const has = !!sel && sel.b - sel.a > 0.005;
    for (const b of [bDel, bKeep, bSil, bFin, bFout, bGain, bPlaySel, bLoop]) b.disabled = !has;
    bUndo.disabled = !history.length; bRedo.disabled = !redo.length;
    bNew.disabled = bOver.disabled = !ops.length;
    histText.textContent = ops.length ? `${ops.length} edit(s) · ${fmt(info.seconds)} s → ${fmt(dur())} s` : 'No edits yet';
    wordsNote.textContent = wordsState === 'loading' ? 'Finding the words with Whisper…' : wordsState === 'off' ? 'Words: set up Whisper (Check takes) to see the words here' : wordsState === 'failed' ? 'Whisper couldn\'t find the words' : words && words.length ? 'Words from Whisper: click one to select it' : 'Whisper heard no words';
  }
  const head = h('div.te-head');
  inner.append(head);
  function drawHead() {
    const t = playing ? playing.from + (ctx.currentTime - playing.start) : cursor;
    head.style.left = tx(Math.min(t, dur())) + 'px';
  }

  // ---------------------------------------------------------------- selection
  function quietest(t) {
    const rate = buf.sampleRate, d = buf.getChannelData(0), win = Math.round(rate * 0.005), span = Math.round(rate * 0.025);
    const c = Math.round(t * rate);
    let best = c, bestE = Infinity;
    for (let p = Math.max(win, c - span); p <= Math.min(d.length - win, c + span); p += Math.max(1, Math.round(win / 4))) {
      let e = 0; for (let i = p - win; i < p + win; i += 2) e += d[i] * d[i];
      e += 1e-7 * Math.abs(p - c); // closer wins a tie
      if (e < bestE) { bestE = e; best = p; }
    }
    return best / rate;
  }
  const snapSel = (s) => {
    if (!snap || !s) return s;
    const a = s.a <= 0.001 ? 0 : quietest(s.a), b = s.b >= dur() - 0.001 ? dur() : quietest(s.b);
    return b > a ? { a, b } : s;
  };
  function pickWord(w, add) {
    let s = { a: Math.max(0, w.s - 0.02), b: Math.min(dur(), w.e + 0.02) };
    if (add && sel) s = { a: Math.min(sel.a, s.a), b: Math.max(sel.b, s.b) };
    sel = snapSel(s); cursor = sel.a; draw();
  }
  canvas.addEventListener('pointerdown', (e) => {
    const r = canvas.getBoundingClientRect();
    const t0 = xt(e.clientX - r.left);
    const extend = e.shiftKey && sel;
    let moved = false;
    canvas.setPointerCapture(e.pointerId);
    const move = (ev) => {
      const t = xt(ev.clientX - r.left);
      if (!moved && Math.abs(ev.clientX - e.clientX) < 3) return;
      moved = true;
      sel = extend ? { a: Math.min(sel.a, t), b: Math.max(sel.b, t) } : { a: Math.min(t0, t), b: Math.max(t0, t) };
      draw();
    };
    const up = () => {
      canvas.removeEventListener('pointermove', move); canvas.removeEventListener('pointerup', up);
      if (!moved) { if (extend) sel = { a: Math.min(sel.a, t0), b: Math.max(sel.b, t0) }; else { sel = null; cursor = t0; } }
      if (sel) { sel = snapSel(sel); cursor = sel.a; }
      draw();
    };
    canvas.addEventListener('pointermove', move); canvas.addEventListener('pointerup', up);
  });
  scroller.addEventListener('wheel', (e) => { if (e.ctrlKey) { e.preventDefault(); setZoom(zoom * (e.deltaY < 0 ? 1.25 : 0.8)); } }, { passive: false });
  function setZoom(z) {
    const mid = (scroller.scrollLeft + scroller.clientWidth / 2) / width();
    zoom = Math.max(1, Math.min(40, z));
    draw();
    scroller.scrollLeft = mid * width() - scroller.clientWidth / 2;
  }

  // ---------------------------------------------------------------- playback
  let raf = 0;
  function stop() {
    if (playing) { try { playing.node.onended = null; playing.node.stop(); } catch { } playing = null; }
    cancelAnimationFrame(raf);
    bPlay.textContent = '▶ Play';
    drawHead();
  }
  function togglePlay(selection) {
    if (playing) { stop(); return; }
    if (ctx.state === 'suspended') ctx.resume();
    const from = selection && sel ? sel.a : sel ? sel.a : cursor >= dur() - 0.01 ? 0 : cursor;
    const to = selection && sel ? sel.b : dur();
    const node = ctx.createBufferSource();
    node.buffer = buf; node.connect(ctx.destination);
    node.start(0, from, Math.max(0.01, to - from));
    playing = { node, start: ctx.currentTime, from, to };
    bPlay.textContent = '■ Stop';
    node.onended = () => { const again = loop && selection && sel; playing = null; stop(); if (again) togglePlay(true); };
    const tick = () => { drawHead(); if (playing) raf = requestAnimationFrame(tick); };
    tick();
  }

  // ---------------------------------------------------------------- edits
  function doOp(kindOp, extra = {}) {
    if (!sel || sel.b - sel.a < 0.005) return;
    stop();
    const op = { op: kindOp, a: +sel.a.toFixed(5), b: +sel.b.toFixed(5), ...extra };
    history.push({ buf, ops: ops.slice(), words, sel, cursor });
    redo.length = 0;
    buf = applyOp(ctx, buf, op);
    ops.push(op);
    if (words) words = mapWords(words, op);
    if (kindOp === 'delete') { cursor = op.a; sel = null; } else if (kindOp === 'keep') { cursor = 0; sel = null; }
    peaks = null;
    draw();
  }
  function undo() {
    const last = history.pop(); if (!last) return;
    stop();
    redo.push({ buf, ops: ops.slice(), words, sel, cursor });
    ({ buf, ops, words, sel, cursor } = last);
    peaks = null; draw();
  }
  function doRedo() {
    const next = redo.pop(); if (!next) return;
    stop();
    history.push({ buf, ops: ops.slice(), words, sel, cursor });
    ({ buf, ops, words, sel, cursor } = next);
    peaks = null; draw();
  }

  async function save(mode) {
    if (!ops.length) return;
    stop();
    for (const b of [bNew, bOver]) b.disabled = true;
    const was = (mode === 'new' ? bNew : bOver).textContent;
    (mode === 'new' ? bNew : bOver).textContent = 'Saving…';
    try {
      const r = await rpc('take.edit.save', { kind, id, lineId, takeId: info.takeId, mode, ops: ops.map((o) => ({ op: o.op, a: o.a, b: o.b, db: o.db || 0 })) });
      if (r.problem) toast(r.problem, true); else toast(mode === 'new' ? 'Saved as a new take (now in use), with a new .lip.' : 'Take saved, with a new .lip.');
      if (onSaved) onSaved(r);
      closeFn && closeFn(true);
    } catch (e) {
      fail(e);
      (mode === 'new' ? bNew : bOver).textContent = was;
      draw();
    }
  }

  // ---------------------------------------------------------------- keys
  const keys = (e) => {
    if (e.target.matches('input,textarea,select')) return;
    if (e.key === ' ') { e.preventDefault(); togglePlay(false); }
    else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); doOp('delete'); }
    else if (e.ctrlKey && (e.key === 'z' || e.key === 'Z')) { e.preventDefault(); if (e.shiftKey) doRedo(); else undo(); }
    else if (e.ctrlKey && (e.key === 'y' || e.key === 'Y')) { e.preventDefault(); doRedo(); }
  };
  document.addEventListener('keydown', keys, true);
  const ro = new ResizeObserver(() => { peaks = null; draw(); });

  // words (Whisper), while the editor is already usable
  if (info.words) {
    rpc('take.edit.words', { kind, id, lineId, takeId: info.takeId })
      .then((ws) => { words = ws; for (const op of ops) words = mapWords(words, op); wordsState = 'done'; draw(); })
      .catch((e) => { wordsState = 'failed'; wordsNote.title = e.message || String(e); draw(); });
  }

  const result = modal('Edit take', body, [{ label: 'Cancel', value: false }], {
    wide: true, cls: 'te-modal',
    onOpen: (close) => { closeFn = close; requestAnimationFrame(() => { ro.observe(scroller); draw(); }); },
  });
  const v = await result;
  document.removeEventListener('keydown', keys, true);
  ro.disconnect();
  stop();
  ctx.close();
  return v;
}
