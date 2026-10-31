// Proofread lines (Phase 1): step through lines one at a time and fix spelling and grammar. Saved edits go into the
// DBVO Fix plugin like any other edit. Optional AI suggestions come in batches ahead of where the user is; game
// markup (<Alias=…>, (Persuade), [tags]) is locked in the editor and never sent to the AI.
import { rpc, h, clear, modal, toast, fail, helpButton, segmented, inTopDialog, debounce } from './lib.js';

const MARKUP = /<[^<>]*>|\((?:[^()<>]|<[^<>]*>|\([^()]*\))*\)|\[[^[\]]*\]/g;
const BATCH = 50;
const KIND_TEXT = { spelling: 'Spelling', grammar: 'Grammar', punctuation: 'Punctuation', style: 'Style — check before accepting' };

const ONE_TAG = new RegExp('^(?:' + MARKUP.source + ')$');
const tokensOf = (s) => (s || '').match(MARKUP) || [];
const textOf = (l) => l.final ?? l.original ?? '';

/** Text split into plain parts and markup parts, for drawing locked tags. */
function parts(s) {
  const out = [];
  let i = 0;
  for (const m of (s || '').matchAll(MARKUP)) {
    if (m.index > i) out.push({ t: s.slice(i, m.index) });
    out.push({ tag: m[0] });
    i = m.index + m[0].length;
  }
  if (i < (s || '').length) out.push({ t: s.slice(i) });
  return out;
}
const tagChip = (t) => h('span.pr-tag', { contentEditable: 'false', 'data-t': t, title: 'Game markup: locked so it can\'t be changed by accident' }, '🔒 ', t);
const withTags = (s) => parts(s).map((p) => (p.tag ? tagChip(p.tag) : p.t));

/** One-line editor whose game markup is locked chips: typing, deleting and pasting never touch a tag. */
function tagEditor(initial, spellcheck) {
  const el = h('div.pr-box.pr-edit', { contentEditable: 'true', spellcheck: spellcheck ? 'true' : 'false', lang: 'en', role: 'textbox', 'aria-label': 'In-game line' });
  let want = [];
  let good = '';
  const read = (node) => {
    let s = '';
    for (const c of node.childNodes) {
      if (c.nodeType === 3) s += c.nodeValue;
      else if (c.nodeType === 1 && c.dataset && c.dataset.t != null) s += c.dataset.t;
      else if (c.nodeType === 1 && c.tagName !== 'BR') s += read(c);
    }
    return s;
  };
  const api = {
    el,
    get: () => read(el).replace(/\u00a0/g, ' ').trim(), // spacing kept as typed (a doubled space is a fix too)
    set(s) { clear(el).append(...withTags(s)); want = tokensOf(s); good = s; if (api.onInput) api.onInput(); },
    onInput: null,
  };
  const hitsTag = (ranges) => ranges.some((sr) => {
    const r = document.createRange();
    try { r.setStart(sr.startContainer, sr.startOffset); r.setEnd(sr.endContainer, sr.endOffset); } catch { return true; }
    return [...el.querySelectorAll('.pr-tag')].some((t) => r.intersectsNode(t) && !(r.collapsed));
  });
  el.addEventListener('beforeinput', (e) => {
    if (e.inputType === 'insertParagraph' || e.inputType === 'insertLineBreak') { e.preventDefault(); return; }
    if (e.inputType === 'insertFromDrop' || e.inputType === 'deleteByDrag') { e.preventDefault(); return; }
    if (hitsTag(e.getTargetRanges ? e.getTargetRanges() : [])) e.preventDefault();
  });
  el.addEventListener('keydown', (e) => {
    // Backspace / Delete right next to a tag would take it: stop at the tag instead.
    if ((e.key === 'Backspace' || e.key === 'Delete') && !e.ctrlKey) {
      const sel = getSelection();
      if (!sel.rangeCount || !sel.isCollapsed) return;
      const { startContainer: n, startOffset: o } = sel.getRangeAt(0);
      const sib = (node, back) => (back ? node.previousSibling : node.nextSibling);
      let neighbour = null;
      if (n.nodeType === 3) {
        if (e.key === 'Backspace' && o === 0) neighbour = sib(n, true);
        if (e.key === 'Delete' && o === n.nodeValue.length) neighbour = sib(n, false);
      } else if (n === el) neighbour = el.childNodes[e.key === 'Backspace' ? o - 1 : o] || null;
      if (neighbour && neighbour.classList && neighbour.classList.contains('pr-tag')) e.preventDefault();
    }
  });
  el.addEventListener('paste', (e) => {
    e.preventDefault();
    const t = (e.clipboardData.getData('text/plain') || '').replace(/\s*[\r\n]+\s*/g, ' ');
    const sel = getSelection();
    if (sel.rangeCount && hitsTag([sel.getRangeAt(0)])) return;
    document.execCommand('insertText', false, t);
  });
  el.addEventListener('drop', (e) => e.preventDefault());
  el.addEventListener('input', () => {
    // Anything that still got a tag out of place puts the last good text back.
    const now = api.get();
    if (JSON.stringify(tokensOf(now)) !== JSON.stringify(want)) { api.set(good); toast('Game markup can\'t be changed here.', true); }
    else good = now;
    if (api.onInput) api.onInput();
  });
  api.set(initial);
  return api;
}

/** Word-level difference: the base text with removed words struck through and added words marked. */
function diffView(a, b) {
  const tok = (s) => (s || '').match(/<[^<>]*>|\((?:[^()<>]|<[^<>]*>|\([^()]*\))*\)|\[[^[\]]*\]|\s+|[\p{L}\p{N}'’]+|[^\s\p{L}\p{N}]/gu) || [];
  const x = tok(a), y = tok(b);
  const n = x.length, m = y.length;
  const L = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) L[i][j] = x[i] === y[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
  const out = [];
  const piece = (t, cls) => (ONE_TAG.test(t) ? tagChip(t) : cls ? h('span.' + cls, t) : t);
  let i = 0, j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && x[i] === y[j]) { out.push(piece(x[i])); i++; j++; }
    else if (i < n && (j >= m || L[i + 1][j] >= L[i][j + 1])) { out.push(piece(x[i], /^\s+$/.test(x[i]) ? '' : 'pr-del')); i++; }
    else { out.push(piece(y[j], /^\s+$/.test(y[j]) ? '' : 'pr-ins')); j++; }
  }
  return out;
}

/**
 * The Proofread lines dialog.
 * opts: { mod, plugin, shown: lines in the table now, ticked: ticked keys, kindBadge, apply(result), finish(showEdited) }
 */
export async function proofreadDialog(opts) {
  const { mod, plugin } = opts;
  const usable = (l) => l.status !== 'Removed' && l.kind !== 'SkippedEditorId' && textOf(l).trim();
  const shown = opts.shown.filter(usable);
  const notYet = shown.filter((l) => !l.proofread);
  const ticked = plugin.lines.filter((l) => opts.ticked.includes(l.key) && usable(l));
  const ai = (window.__dbvoState && window.__dbvoState.settings.aiProvider) || {};
  const aiLocal = /^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])[:/]/i.test(ai.endpoint || '');
  const aiOk = !!ai.endpoint && (aiLocal || ai.hasKey);

  // ---- setup
  let scope = notYet.length && notYet.length < shown.length ? 'notyet' : 'shown';
  let useSpell = true, useAi = aiOk, skipNone = true;
  const opt = (value, title, n, sub) => {
    const el = h('div.pr-opt' + (scope === value ? '.on' : '') + (n ? '' : '.off'), { onclick: () => { if (!n) return; scope = value; el.parentNode.querySelectorAll('.pr-opt').forEach((x) => x.classList.toggle('on', x === el)); } },
      h('span.pr-radio'), h('div', h('b', title), h('span.faint', ` (${n})`), sub ? h('div.dim', sub) : null));
    return el;
  };
  const check = (on, label, sub, set, disabled) => {
    const box = h('input', { type: 'checkbox', checked: on && !disabled, disabled: !!disabled });
    box.addEventListener('change', () => set(box.checked));
    return h('label.pr-check' + (disabled ? '.off' : ''), box, h('span', h('b', label), sub ? h('span.dim', ' — ', sub) : null));
  };
  const skipRow = check(skipNone, 'Skip lines the AI finds nothing wrong with', 'they count as proofread', (v) => { skipNone = v; });
  skipRow.style.marginLeft = '24px';
  const started = await modal('Proofread lines · ' + plugin.fileName, [
    h('div.dim', 'Steps through lines one at a time so you can fix spelling and grammar. Every edit you save goes into ',
      plugin.isTopicFile ? 'the topic file' : h('b', plugin.fixFileName), ', the same as editing in the panel.'),
    h('div', h('div.field-label', 'Which lines'), h('div.col',
      opt('shown', `The lines shown now`, shown.length, 'Uses the filter and search set in the table.'),
      opt('notyet', 'Only lines not proofread yet', notYet.length, 'Of the lines shown. Lines you went through are left out, so you can stop and pick up later.'),
      opt('ticked', 'Only the ticked lines', ticked.length))),
    h('div', h('div.field-label', 'Help with the text'), h('div.col',
      check(useSpell, 'Spellchecker', 'red underline, right-click a word for suggestions. Free and offline.', (v) => { useSpell = v; }),
      check(useAi, 'AI suggestions', aiOk ? `from your AI provider (${aiLocal ? 'on this PC, free' : (ai.endpoint || '').replace(/^https?:\/\//, '').split('/')[0]}), ${BATCH} lines per request` : 'set up an AI provider first in Global Settings → General & tools → AI auto-tagging (free ones run on your PC)',
        (v) => { useAi = v; skipRow.classList.toggle('off', !v); skipRow.querySelector('input').disabled = !v; }, !aiOk),
      aiOk ? skipRow : null)),
  ], [{ label: 'Cancel', value: false }, { label: 'Start →', primary: true, value: true }], { help: 'proofread-lines' });
  if (!started) return;
  const pool = scope === 'ticked' ? ticked : scope === 'notyet' ? notYet : shown;
  if (!pool.length) { toast('No lines to proofread.'); return; }
  const keys = pool.map((l) => l.key);
  const line = (k) => plugin.lines.find((x) => x.key === k);

  // ---- AI suggestions, fetched in batches ahead of the line shown
  const sugg = new Map(); // key → { text, kind, why } | null (nothing to fix)
  const requested = new Set();
  let aiError = null;
  async function fetchFrom(i) {
    if (!useAi || !open) return;
    // Whole blocks of 50 (the one the line is in and the next), so every request carries a full batch.
    for (let b = Math.floor(i / BATCH); b <= Math.floor(i / BATCH) + 1 && b * BATCH < keys.length; b++) {
      const batch = keys.slice(b * BATCH, b * BATCH + BATCH).filter((k) => !requested.has(k));
      if (!batch.length) continue;
      batch.forEach((k) => requested.add(k));
      paintAi();
      try {
        const r = await rpc('line1.proofread', { modId: mod.id, plugin: plugin.fileName, keys: batch });
        for (const x of r.suggestions) sugg.set(x.key, x.text ? { text: x.text, kind: x.kind, why: x.why } : null);
        for (const k of batch) if (!sugg.has(k)) sugg.set(k, null);
        aiError = null;
      } catch (e) {
        batch.forEach((k) => requested.delete(k));
        aiError = e && e.message ? e.message : String(e);
      }
      if (!open) return;
      if (batch.includes(keys[idx])) onSuggestionArrived();
      paintAi();
      if (aiError) return;
    }
  }

  // ---- stepping
  let idx = 0, open = true;
  const stats = { seen: new Set(), edited: new Set(), accepted: new Set(), declined: new Set(), autoSkipped: new Set(), skipped: new Set() };
  let editor = null, accepted = false, base = '', busy = false, cameBack = false;
  const startText = new Map(keys.map((k) => [k, textOf(line(k) || {}).trim()]));
  const view = h('div.pr-step');
  const head = h('div.modal-head');
  const prog = h('div.pr-prog', h('i'));
  const aiSlot = h('div');
  const keysRow = h('div.pr-keys', h('kbd', 'Ctrl'), '+', h('kbd', 'Enter'), ' save & next · ', h('kbd', 'Alt'), '+', h('kbd', '→'), ' skip · ', h('kbd', 'Alt'), '+', h('kbd', '←'), ' back',
    useAi ? [' · ', h('kbd', 'Alt'), '+', h('kbd', 'A'), ' accept suggestion'] : null);
  const foot = h('div.row');
  const ruleSlot = h('div');
  // The xEdit script's verdict on the text in the box, as you type (a line it can't take stays silent in game).
  let ruleSeq = 0;
  const checkRules = debounce(async () => {
    const seq = ++ruleSeq;
    const text = editor ? editor.get() : '';
    let r = null;
    try { r = await rpc('rules.check', { text }); } catch { /* the check is a hint only */ }
    if (seq !== ruleSeq || !open) return;
    clear(ruleSlot);
    if (r && r.problem && !r.isPlayerName) ruleSlot.append(h('div.banner.bad', { style: { margin: 0 } }, h('span', { style: { whiteSpace: 'pre-line' } }, h('b', 'DBVO can\'t voice this as written: '), r.problem)));
  }, 250);
  let close = null;

  const dirty = () => !!editor && editor.get() !== base.trim();

  // One save at a time (a held Ctrl+Enter or a double click doesn't pass over the next line).
  async function save(next) {
    if (busy) return false;
    const at = idx, k = keys[at];
    const l = line(k);
    if (!l) return true;
    const text = editor.get();
    const changed = text !== base.trim();
    busy = true;
    try {
      const r = await rpc('line1.update', { modId: mod.id, plugin: plugin.fileName, key: l.key, action: 'proofread', text: changed ? text : null });
      opts.apply(r);
      stats.seen.add(k);
      stats.skipped.delete(k);
      const s = sugg.get(k);
      if (s) {
        if (accepted && text === s.text) { stats.accepted.add(k); stats.declined.delete(k); }
        else if (changed || !stats.accepted.has(k)) { stats.accepted.delete(k); stats.declined.add(k); }
      }
      busy = false; // before moving on: the next line may be passed over at once
      if (next && idx === at) go(at + 1);
      return true;
    } catch (e) { fail(e); return false; } finally { busy = false; }
  }
  // Skip leaves the line as it is and not proofread; text typed in it is saved instead.
  async function skip() {
    if (busy) return;
    if (dirty()) { await save(true); return; }
    stats.skipped.add(keys[idx]);
    go(idx + 1);
  }
  async function back() {
    if (busy || idx === 0) return;
    if (dirty() && !(await save(false))) return;
    go(idx - 1, false);
  }
  function go(i, forward = true) {
    if (!open) return;
    if (i >= keys.length) { finish(); return; }
    idx = Math.max(0, i);
    cameBack = !forward;
    render();
    fetchFrom(idx);
    if (forward) autoSkipIfNothing();
  }
  // Moving on: a line the AI found nothing wrong with is passed over (when asked), unless the user typed in it,
  // came back to it, or skipped it before.
  async function autoSkipIfNothing() {
    const k = keys[idx];
    if (!open || busy || cameBack || !useAi || !skipNone || !sugg.has(k) || sugg.get(k) !== null || dirty() || stats.seen.has(k) || stats.skipped.has(k)) return false;
    const l = line(k);
    busy = true;
    try { opts.apply(await rpc('line1.update', { modId: mod.id, plugin: plugin.fileName, key: l.key, action: 'proofread' })); } catch (e) { fail(e); return false; } finally { busy = false; }
    stats.autoSkipped.add(k);
    stats.seen.add(k);
    if (open && keys[idx] === k && !dirty()) go(idx + 1);
    return true;
  }
  async function onSuggestionArrived() { if (!(await autoSkipIfNothing())) { paintAi(); paintFoot(); } }

  function paintAi() {
    if (!open) return;
    clear(aiSlot);
    if (!useAi) return;
    const k = keys[idx];
    const l = line(k);
    if (aiError && !sugg.has(k)) {
      aiSlot.append(h('div.banner.warn', { style: { margin: 0 } }, h('span', 'AI suggestions failed: ', aiError, ' The spellchecker still works.'), h('div.spacer'),
        h('button.btn.sm', { onclick: () => { aiError = null; fetchFrom(idx); } }, 'Retry'),
        h('button.btn.sm', { onclick: () => { useAi = false; aiError = null; render(); } }, 'Turn AI off for this run')));
      return;
    }
    if (!sugg.has(k)) {
      aiSlot.append(h('div.pr-sugg.wait', h('div.row', h('span.pr-spin'), h('span.dim', `Getting AI suggestions for lines ${idx + 1}–${Math.min(keys.length, idx + BATCH)}… You can edit while you wait.`))));
      return;
    }
    const s = sugg.get(k);
    if (!s) { aiSlot.append(h('div.pr-sugg', h('div.row', h('b.pr-ai', 'AI'), h('span.dim', l && l.final != null && l.final !== l.original ? 'Nothing more to fix in your edit.' : 'Nothing to fix in this line.')))); return; }
    const style = s.kind === 'style';
    const acceptBtn = h('button.btn.sm' + (style ? '' : '.primary'), { title: 'Use the suggestion (Alt+A). Nothing is saved until you go to the next line.', disabled: accepted, onclick: () => accept() }, accepted ? 'Accepted' : 'Accept');
    aiSlot.append(h('div.pr-sugg' + (style ? '.style' : ''),
      h('div.row', h('b.pr-ai', 'AI suggestion'), h('span.pill.' + (style ? 'p-warn' : 'p-ok'), KIND_TEXT[s.kind] || s.kind), h('div.spacer'),
        h('button.btn.sm', { title: 'Put the suggestion in the box to change it further', onclick: () => { editor.set(s.text); accepted = false; editor.el.focus(); paintFoot(); } }, 'Edit it'), acceptBtn),
      h('div.pr-text', diffView(base, s.text)),
      style ? h('div.dim.pr-why', 'Not a mistake, only a style change', s.why ? ` (${s.why})` : '', '. Keeping the original is often right: it may be the character\'s way of speaking.')
        : s.why ? h('div.dim.pr-why', s.why) : null,
      tokensOf(base).length ? h('div.dim.pr-why', '🔒 Game markup is locked: the AI never sees or changes it.') : null,
      accepted ? h('div.pr-ok', '✓ Accepted. Saved when you go to the next line.') : null));
  }
  function accept() {
    const s = sugg.get(keys[idx]);
    if (!s) return;
    editor.set(s.text);
    accepted = true;
    paintAi();
    paintFoot();
  }

  function paintFoot() {
    const s = useAi ? sugg.get(keys[idx]) : null;
    const keepLabel = s && !accepted && !dirty() ? 'Keep original & next →' : 'Save & next →';
    clear(foot).append(
      h('button.btn', { disabled: idx === 0, title: 'The line before (Alt+←); text you typed here is saved first', onclick: back }, '← Back'),
      h('button.btn', { title: 'Leave this line as it is and not proofread (Alt+→); text you typed here is saved instead', onclick: skip }, 'Skip'),
      h('div.spacer'),
      h('button.btn.ghost', { title: 'Close; the lines you saved stay saved', onclick: async () => { if (dirty() && !(await save(false))) return; finish(); } }, 'Stop for now'),
      h('button.btn.primary', { title: 'Save this line and go to the next (Ctrl+Enter)', onclick: () => save(true) }, keepLabel));
  }

  function render() {
    if (!open) return;
    const l = line(keys[idx]);
    if (!l) { go(idx + 1); return; }
    base = textOf(l);
    accepted = false;
    const earlier = l.final != null && l.final !== l.original;
    clear(head).append(h('h2', 'Proofread lines · ' + plugin.fileName), h('span.dim', `${idx + 1} of ${keys.length}`), helpButton('proofread-lines'),
      h('button.btn.ghost.sm', { title: 'Close', onclick: async () => { if (dirty() && !(await save(false))) return; finish(); } }, '✕'));
    prog.firstChild.style.width = (100 * (idx + 1)) / keys.length + '%';
    editor = tagEditor(base, useSpell);
    checkRules();
    editor.onInput = () => { checkRules(); if (accepted) { const s = sugg.get(keys[idx]); if (!s || editor.get() !== s.text) { accepted = false; paintAi(); } } paintFoot(); };
    const fixRow = plugin.isTopicFile || !earlier ? null : h('div.row',
      h('span.dim', 'Back to'), h('button.btn.sm', { title: 'Put the mod\'s original text in the box', onclick: () => { editor.set(l.original); accepted = false; paintFoot(); } }, 'Original text'),
      h('div.spacer'), h('span.dim', 'DBVO Fix'),
      segmented([[true, 'On', 'Write this line\'s edit to ' + plugin.fixFileName], [false, 'Off', 'Keep the plugin\'s original text in game']], !!l.inFix, async (v) => {
        try { opts.apply(await rpc('line1.update', { modId: mod.id, plugin: plugin.fileName, key: l.key, action: 'fix', value: v })); } catch (e) { fail(e); }
      }));
    clear(view).append(head, prog,
      h('div.pr-meta', l.formId ? h('span.mono', l.formId) : null, opts.kindBadge(l), l.inFix ? h('span.pill.p-acc', 'In DBVO Fix') : null, l.proofread ? h('span.pill.p-ok', 'Proofread before') : null,
        stats.autoSkipped.size ? h('span.pill.p-acc', { title: 'Lines the AI found nothing wrong with were passed over and count as proofread' }, `Skipped ${stats.autoSkipped.size} with nothing to fix`) : null,
        h('div.spacer'), plugin.isTopicFile ? h('span.dim', 'Edits go into the topic file') : h('span.dim', 'Fix → ', h('b.link', plugin.fixFileName))),
      h('div', h('div.field-label', 'Original'), h('div.pr-box.pr-orig', withTags(l.original))),
      earlier ? h('div', h('div.field-label', 'Your earlier edit'), h('div.pr-box', withTags(l.final))) : null,
      aiSlot,
      h('div', h('div.field-label', 'In-game line (edit here)'), editor.el),
      ruleSlot,
      fixRow, keysRow, foot);
    paintAi();
    paintFoot();
    requestAnimationFrame(() => { editor.el.focus(); const r = document.createRange(); r.selectNodeContents(editor.el); r.collapse(false); const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r); });
  }

  const onKey = (e) => {
    if (!open || !view.isConnected) return;
    if (!inTopDialog(view)) return; // another dialog (a question) is on top
    const stop = () => { e.preventDefault(); e.stopPropagation(); };
    if (e.ctrlKey && !e.altKey && e.key === 'Enter') { stop(); if (!e.repeat) save(true); }
    else if (e.altKey && !e.ctrlKey && !e.shiftKey && e.key === 'ArrowRight') { stop(); skip(); } // Alt+arrows: not the app's Back/Forward here
    else if (e.altKey && !e.ctrlKey && !e.shiftKey && e.key === 'ArrowLeft') { stop(); back(); }
    else if (e.altKey && !e.ctrlKey && (e.key === 'a' || e.key === 'A')) { stop(); accept(); }
  };
  document.addEventListener('keydown', onKey, true);

  function finish() {
    if (!open) return;
    open = false;
    document.removeEventListener('keydown', onKey, true);
    if (close) close('done');
  }

  go(0);
  // Escape or going to another page: the line being typed in is saved first (a failed save keeps the dialog open).
  const r = await modal('Proofread lines', [view], [], { cls: 'pr-card', autofocus: false, onOpen: (c) => { close = c; },
    guard: async () => { if (busy || (dirty() && !(await save(false)))) return false; open = false; return true; } });
  // Closed without asking (the help's "Open in the guide"): keep what was typed.
  if (r === null && open && dirty()) await save(false);
  open = false;
  document.removeEventListener('keydown', onKey, true);
  if (r !== 'done' && r !== null) return;
  // ---- summary: after the key or click that closed the dialog is handled, and only on the page it was opened from
  await new Promise((res) => setTimeout(res, 60));
  if (opts.isCurrent && !opts.isCurrent()) return;
  if (!stats.seen.size && !stats.skipped.size) { opts.finish(false); return; } // closed before doing anything
  stats.edited = new Set(keys.filter((k) => line(k) && textOf(line(k)).trim() !== startText.get(k)));
  const showEdited = await modal('Proofreading done · ' + plugin.fileName, [
    h('table.pr-sum',
      h('tr', h('td', 'Lines gone through'), h('td', stats.seen.size)),
      h('tr', h('td', 'Edited and saved', plugin.isTopicFile ? '' : [' to ', h('b.link', plugin.fixFileName)]), h('td', stats.edited.size)),
      useAi || stats.accepted.size ? h('tr', h('td.dim', { style: { paddingLeft: '18px' } }, 'of these, AI suggestions you accepted'), h('td.dim', stats.accepted.size)) : null,
      useAi || stats.declined.size ? h('tr', h('td', 'AI suggestions you turned down'), h('td', stats.declined.size)) : null,
      stats.autoSkipped.size ? h('tr', h('td', 'Skipped, AI found nothing to fix'), h('td', stats.autoSkipped.size)) : null,
      h('tr', h('td', 'Skipped by you'), h('td', stats.skipped.size)),
      h('tr', h('td', 'Not reached'), h('td', keys.length - new Set([...stats.seen, ...stats.skipped]).size))),
    h('div.dim', 'Edited lines are under the ', h('b', 'Edited by you'), ' filter. Lines you skipped stay not proofread, so a later run with ', h('b', 'Only lines not proofread yet'), ' picks them up.'),
  ], [...(stats.edited.size ? [{ label: `Show the ${stats.edited.size} edited line(s)`, value: true }] : []), { label: 'Close', primary: true, value: false }]);
  opts.finish(!!showEdited);
}
