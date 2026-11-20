// Phase 2 → Variants: per voice pack, which lines play a random take in game, making more with the Variant settings,
// lines added by hand, and the Variants Pack (one BSA + its plugin) with its pre-ticked FOMOD option.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, switchEl, popupMenu, icon, helpButton, searchLamp } from './lib.js';
import { app, go } from './app.js';
import { voiceChip } from './phase2.js';

const STAB = [[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']];
const stabName = (v) => (v < 0.25 ? 'Creative' : v < 0.75 ? 'Natural' : 'Robust');
const LEADING = /^\s*((?:\[[^\]]*\]\s*)*)/;

/** Mirrors VariantsService.NormalizePrefix: whole [tags] stay (empty ones go); anything else becomes one tag. */
export function normalizePrefix(prefix) {
  const p = (prefix || '').trim().replace(/\s+/g, ' ');
  if (!p) return '';
  const tags = p.match(/\[[^\]]*\]/g) || [];
  const rest = p.replace(/\[[^\]]*\]/g, '').trim();
  if (tags.length && !rest) return tags.map((t) => t.slice(1, -1).trim()).filter(Boolean).map((t) => '[' + t + ']').join(' ');
  const inner = p.replace(/[[\]]/g, ' ').replace(/\s+/g, ' ').trim();
  return inner ? '[' + inner + ']' : '';
}

/** The text a variant is sent as; mirrors VariantsService.VariantText (the backend makes the real one). */
export function variantText(spoken, prefix, word) {
  const m = LEADING.exec(spoken || '');
  const words = (spoken || '').slice(m[0].length).trim();
  let lead = normalizePrefix(prefix);
  if (!lead) lead = m[1].trim().replace(/\s+/g, ' ');
  const w = (word || '').trim().replace(/^\[|\]$/g, '').trim();
  if (w) {
    const tags = [...lead.matchAll(/\[[^\]]*\]/g)];
    if (!tags.length) lead = (lead + ' [' + w + ']').trim();
    else {
      const last = tags[tags.length - 1];
      const inner = last[0].slice(1, -1).trim();
      const esc = w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      if (!new RegExp('(^|[\\s,])' + esc + '($|[\\s,])', 'i').test(inner))
        lead = lead.slice(0, last.index) + '[' + (inner ? inner + ', ' + w : w) + ']' + lead.slice(last.index + last[0].length);
    }
  }
  return lead ? lead + ' ' + words : words;
}

const billed = (text, ctx) => { if (!ctx) return text.length; const m = LEADING.exec(text); return m[0].length < text.length ? text.length - m[0].length : text.length; };

export async function variantsView(voiceId) {
  let res;
  try { res = await rpc('variants.get', { voiceId }); } catch (e) { fail(e); return h('div.view', h('div.view-body', h('div.empty', h('h2', 'Variants'), h('div', String(e.message || e))))); }
  let data = res.data;
  const rate = res.creditRate;
  const voice = data.voice;
  const st = data.settings;
  const legacy = voice.naming === 'Legacy';
  const eleven = voice.engine === 'elevenlabs';
  // eleven_v3 (and other engines) take no context: the prefix then always goes in the text, billed.
  const ctxAvail = !!st.contextAvailable;
  // The full spoken text of a list row: its leading tags, then the words (mid-line tags included).
  const fullOf = (l) => ((l.lead ? l.lead + ' ' : '') + (l.words || '')).trim();
  const view = h('div.view.var-page');

  const packBtn = h('button.btn', { title: 'Build the Variants Pack (one BSA with every line\'s variants) and add it to your FOMOD' }, 'Variants Pack ▾');
  view.append(h('div.view-head',
    h('button.btn.sm.ghost', { title: 'Back to the patch list', onclick: () => go('patches') }, '‹ Patches'),
    h('h1', 'Variants'), voiceChip(voice.name, voice.color), helpButton('variants'),
    h('span.sub', legacy ? 'Lines that play a random take each time. One Variants Pack covers every patch of this voice pack.' : 'Lines that play a random take each time. Every patch with a line exports its own ★ takes; new variants go to the one with the most ★.'),
    h('div.spacer'), legacy ? packBtn : null));

  if (voice.naming === 'Dbrev') {
    view.append(h('div.view-body', h('div.empty', h('h2', 'DBReV packs make variants in each patch'),
      h('div', 'DBReV finds lines by their ID. Star (★) extra takes of a line in its patch: each one is exported as a variation, (1), (2)…'))));
    return view;
  }

  // ---- counts
  const kpis = h('div.var-kpis');
  // ---- filters
  let filter = store.get('variants-filter:' + voiceId, 'suggested');
  let sortBy = store.get('variants-sort:' + voiceId, 'used');
  const chips = h('div.row', { style: { gap: '6px' } });
  const q = h('input', { type: 'text', placeholder: 'Search spoken text', style: { width: '240px' } });
  const sortSel = h('select', { title: 'Order of the list' }, h('option', { value: 'used' }, 'Most used first'), h('option', { value: 'short' }, 'Shortest first'), h('option', { value: 'az' }, 'A–Z'), h('option', { value: 'variants' }, 'Most variants first'));
  sortSel.value = sortBy;
  const addBtn = h('button.btn.sm', { title: 'Type a line from the game: it gets the voice pack\'s file name and can have variants (they go in the Variants Pack)' }, '+ Add a line…');
  const toolbar = h('div.toolbar', { style: { alignItems: 'center' } }, chips, searchLamp(q), legacy ? addBtn : null, h('div.spacer'), h('span.dim', 'Sort'), sortSel);
  view.append(kpis, toolbar);
  if (data.unreadable && data.unreadable.length)
    view.append(h('div.banner.warn', { style: { margin: '0 0 6px' } }, `${data.unreadable.length} patch(es) couldn't be read, so their lines are missing here: ${data.unreadable.join(', ')}. Open them in Phase 2 to see why.`));

  const split = h('div.split');
  const left = h('div.left');
  const insp = h('aside.inspector');
  split.append(left, insp);
  view.append(split);

  const isSuggested = (l) => l.kept < 2 && !l.added && l.mods >= 2;
  const hasVariants = (l) => l.kept >= 2;
  const needsLook = (l) => l.pending > 0;
  const shown = () => {
    if (filter === 'added' && !data.lines.some((l) => l.added)) { filter = 'all'; store.set('variants-filter:' + voiceId, filter); }
    const f = q.value.trim().toLowerCase();
    let rows = data.lines.filter((l) => (filter === 'all' || (filter === 'suggested' && isSuggested(l)) || (filter === 'has' && hasVariants(l)) || (filter === 'look' && needsLook(l)) || (filter === 'added' && l.added))
      && (!f || l.text.toLowerCase().includes(f) || l.words.toLowerCase().includes(f)));
    const cmp = { used: (a, b) => b.mods - a.mods || b.patches - a.patches || a.text.length - b.text.length,
      short: (a, b) => a.text.length - b.text.length || a.text.localeCompare(b.text),
      az: (a, b) => a.text.localeCompare(b.text), variants: (a, b) => b.kept - a.kept || b.mods - a.mods }[sortBy];
    return rows.sort(cmp);
  };

  const reach = (l) => {
    const out = [];
    if (l.pending) out.push(h('span.badge.b-toolong', { title: `${l.pending} take(s) made with Creative stability not played yet: play them in the panel on the right and star the good ones` }, `${l.pending} to listen to`));
    if (l.kept >= 2) out.push(...(legacy ? [h('span.badge.b-valid', { title: 'Once you build the Variants Pack, Dragonborn Voice Over 2 plays one of these at random (numbered files in the line\'s folder)' }, 'DBVO 2'), h('span.badge.b-valid', { title: 'DBReV L: Dragonborn ReVoiced playing this DBVO 1 (Legacy) pack. Once you build the Variants Pack, it plays one of these at random (name(1), name(2)… next to the line\'s file)' }, 'DBReV L')]
      : [h('span.badge.b-valid', { title: 'Exported with its patch as numbered files in the line\'s folder: DBVO 2 plays one at random' }, 'DBVO 2')]));
    if (l.added) out.push(legacy ? h('span.badge.b-fixed', { title: 'No patch exports this line: the Variants Pack writes its file too' }, 'Added here')
      : h('span.badge.b-broken', { title: 'Added by hand: with DBVO 2.0 names it can\'t ship. Remove it in the panel on the right.' }, 'Added here'));
    return out.length ? h('span.row', { style: { gap: '4px' } }, out) : h('span.faint', '–');
  };

  let focusKey = null;
  const grid = new VGrid(left, {
    storageKey: 'variants:' + voiceId,
    rowKey: (l) => l.key,
    rowClass: () => '',
    onRowClick: (l, e) => {
      // No ticks: the line you click is the one Generate works on. Ctrl+click or Shift+click picks several.
      if (!(e && (e.ctrlKey || e.shiftKey || e.key === ' '))) { grid.selected = new Set([String(l.key)]); grid.paint(true); grid.el.dispatchEvent(new CustomEvent('selection')); }
      focusKey = l.key; renderInspector(); renderPreview();
    },
    columns: [
      { id: 'text', title: 'Spoken text', flex: 1, width: 320, sort: (l) => l.text.toLowerCase(), render: (l) => h('span', { title: `In-game line: ${l.text}\nFile: ${l.stem}.fuz` }, l.words || l.text) },
      { id: 'mods', title: 'Said in', width: 100, sort: (l) => -l.mods, render: (l) => h('span.dim', { title: `${l.mods} mod(s), ${l.patches} patch(es) of this voice pack. With the same file name they all play the same variants.` }, l.added ? '–' : `${l.mods} mod${l.mods === 1 ? '' : 's'}`) },
      { id: 'kept', title: 'Variants', width: 110, sort: (l) => -l.kept, render: (l) => l.kept ? h('span.row', { style: { gap: '4px', color: 'var(--warn)' }, title: `${l.kept} take(s) starred (★) as variants · ${l.takes} take(s) in all` }, icon('star', { filled: true, size: 14 }), String(l.kept)) : h('span.dim', `${l.takes} take${l.takes === 1 ? '' : 's'}`) },
      { id: 'reach', title: 'Reaches', width: 200, sortable: false, render: reach },
    ],
  });

  const renderCounts = () => {
    const withV = data.lines.filter(hasVariants);
    const look = data.lines.filter(needsLook).length;
    const sugg = data.lines.filter(isSuggested).length;
    const added = data.lines.filter((l) => l.added).length;
    clear(kpis).append(
      h('div.kpi', h('b', withV.length.toLocaleString()), h('span', 'lines with variants')),
      h('div.kpi', h('b', withV.reduce((a, l) => a + l.kept, 0).toLocaleString()), h('span', 'variant takes ★')),
      h('div.kpi', h('b', sugg.toLocaleString()), h('span', 'suggested lines')),
      h('div.kpi' + (look ? '.warn' : ''), h('b', look.toLocaleString()), h('span', 'need a look')));
    const chip = (key, label, n, title) => h('span.chip' + (filter === key ? '.on' : ''), { title, onclick: () => { filter = key; store.set('variants-filter:' + voiceId, key); renderCounts(); grid.setRows(shown()); } }, label, h('span.n', n.toLocaleString()));
    clear(chips).append(
      chip('suggested', 'Suggested', sugg, 'Lines the same in two or more mods, without variants yet: one set of variants covers them all'),
      chip('has', 'Has variants', withV.length, 'Lines with two or more ★ takes'),
      chip('look', 'Needs a look', look, "Lines with Creative takes you have not played yet"),
      added ? chip('added', 'Added here', added, 'Lines you typed in') : null,
      chip('all', 'All lines', data.lines.length, 'Every line of this voice pack'));
  };

  // ---- inspector (one line)
  const audio = new Audio();
  let lineData = null;
  let ticket = 0;
  let drawnKey = null;
  async function renderInspector() {
    const mine = ++ticket;
    // The same line drawn again (after play, ★ or delete): it stays where you scrolled, and stays on screen while it reloads.
    const same = focusKey != null && focusKey === drawnKey;
    const scrollTop = same ? insp.scrollTop : 0;
    if (!same) { clear(insp); drawnKey = null; }
    if (!focusKey) { insp.append(h('div.help', h('p', h('b', 'Pick a line'), ' to hear its takes and star (★) the ones that should play in game.'), h('p', 'Click a line (Ctrl+click or Shift+click for several) and press Generate variants to make new takes with the Variant settings below.'))); return; }
    const key = focusKey;
    let got;
    try { got = await rpc('variants.line', { voiceId, key }); } catch (e) { if (mine === ticket) fail(e); return; }
    // a newer drawing started meanwhile (another click, a reload): it draws, this one doesn't
    if (mine !== ticket || key !== focusKey) return;
    clear(insp);
    lineData = got;
    const d = lineData;
    // the stars that ship (DBVO 2.0 names: only the home patch's); Play all plays those
    const kept = d.takes.filter((t) => t.kept && t.counts);
    renderPreview();
    insp.append(h('h3', d.text),
      h('div.kv2',
        h('div', 'Said in'), h('div.kv-v', { title: d.modNames.join('\n') }, d.added ? 'Added here' : d.modNames.join(', ') || '–'),
        h('div', 'File name'), h('div.kv-v.mono', d.stem + '.fuz'),
        kept.length < 2 ? [h('div', 'Variants'), h('div.kv-v.faint', legacy ? 'none yet: two or more ★ go into the Variants Pack' : 'none yet: two or more ★ are exported with the patch')]
          : legacy ? [h('div', 'DBVO 2'), h('div.kv-v.mono', `${d.stem}\\1.fuz … ${kept.length}.fuz`), h('div', 'DBReV L'), h('div.kv-v.mono', `${d.stem}(1).fuz … (${Math.min(kept.length, 19)})`)]
          : [h('div', 'DBVO 2'), h('div.kv-v.mono', `${d.stem}\\0.fuz … ${kept.length - 1}.fuz`), h('div', 'Patch'), h('div.kv-v', d.homePatch || 'none: added by hand, it can\'t ship')]),
      h('h3', legacy ? `Takes · ${kept.length} ★` : `Takes · ${kept.length} ★ in ${d.homePatch || 'no patch'}`));
    for (const t of d.takes) {
      const starTip = legacy
        ? (t.kept ? 'A variant (★): click to take it out' : 'Star it: with two or more ★ the line gets variants in the Variants Pack')
        : (t.kept ? 'Exported by its patch (★): click to take it out' : 'Star it: its patch exports its ★ takes. One ★ is the line\'s file (in place of the take in use); two or more play at random.');
      const star = h('button.act-btn' + (t.kept ? '.on.warn' : ''), { title: starTip, onclick: async () => {
        try { await rpc('variants.keep', { voiceId, patchId: t.patchId || '', lineId: t.lineId, takeId: t.takeId, kept: !t.kept }); await reload(); } catch (e) { fail(e); }
      } }, icon('star', { filled: t.kept }));
      const meta = [`${t.duration.toFixed(1)} s`, t.model || t.engine, t.engine === 'elevenlabs' ? stabName(t.stability) : null, t.speed && t.speed !== 1 ? `×${t.speed}` : null, t.seed != null ? `seed ${t.seed}` : null, t.inUse ? 'in use' : null].filter(Boolean).join(' · ');
      insp.append(h('div.take' + (t.inUse ? '.sel' : ''), { title: t.spoken },
        h('button.act-btn', { title: t.pending ? "Play (clears listen)" : "Play", onclick: async () => {
          audio.onended = null; // stops a running Play all
          audio.src = t.url + '?t=' + Date.now();
          try { await audio.play(); } catch (e) { toast("The take couldn't be played.", true); return; }
          if (!t.pending) return;
          try { await rpc('variants.listened', { voiceId, patchId: t.patchId || '', lineId: t.lineId, takeId: t.takeId }); await reload(); } catch (e) { fail(e); }
        } }, icon('play')),
        h('span.take-lbl', '#' + t.slot),
        h('span.meta', { style: { flex: 1 } }, meta, d.patchNames.length > 1 && t.patchId ? h('span.faint', ' · ' + t.patch) : null),
        t.pending ? h('span.badge.b-toolong', { title: "Made with Creative stability, so it may drift out of the voice. Play it to clear this. Star it only if it keeps the voice." }, 'listen') : null,
        t.qaFlags && t.qaFlags.length ? h('span.badge.b-broken', { title: 'Check takes flagged it: ' + t.qaFlags.join(', ') }, '⚠') : null,
        star,
        h('button.act-btn', { title: t.inUse && t.patchId ? "The take in use can't be deleted here: pick another take for this line in its patch (Phase 2) first" : 'Delete this take and its files', disabled: !!(t.inUse && t.patchId), onclick: async () => {
          if (!(await confirmBox('Delete take?', `Delete take #${t.slot} of "${d.text}"${t.patch ? ' in ' + t.patch : ''} and its files? This can't be undone.`, 'Delete', true))) return;
          try {
            const r = await rpc('variants.deleteTake', { voiceId, patchId: t.patchId || '', lineId: t.lineId, takeId: t.takeId });
            if (r && r.left) toast(`The take is deleted, but Windows kept its folder (a file in it is still open): ${r.left}`, true);
            await reload();
          } catch (e) { fail(e); }
        } }, '🗑')));
    }
    const playAll = h('button.btn.sm', { title: 'Play every ★ take, one after another', disabled: !kept.length, onclick: () => {
      let i = 0; const next = () => { if (i >= kept.length) { audio.onended = null; return; } audio.src = kept[i++].url + '?t=' + Date.now(); audio.play().catch(() => {}); }; audio.onended = next; next();
    } }, '▶ Play all ★');
    const more = h('button.btn.sm', { title: 'One more take for this line, with one row of the Variant settings', onclick: () => popupMenu(more, rows.map((r, i) => ({ label: `Row ${i + 1}: ${stabName(r.stability)} · ×${Number(r.speed).toFixed(2)}${r.word ? ' · ' + r.word : ''}`, sub: variantText(d.spoken, prefix.value, r.word), onclick: () => generate([d.key], i) }))) }, '+ One more take');
    insp.append(h('div.row', playAll, more),
      d.canRemove ? h('button.btn.sm.danger', { title: 'Remove the copy of this line you added here, with its takes', onclick: async () => {
        if (!(await confirmBox('Remove this line?', `The copy of "${d.text}" you added here, and its takes, are deleted. Takes in patches stay. This can't be undone.`, 'Remove', true))) return;
        try { await rpc('variants.removeLine', { voiceId, key: d.key }); focusKey = null; await reload(); } catch (e) { fail(e); }
      } }, 'Remove this line') : null,
      h('div.help', h('p', h('b', '★ = variant.'), legacy
        ? ' A line with two or more ★ takes goes into the Variants Pack when you build it. In game, DBVO 2 and DBReV L (Dragonborn ReVoiced playing this Legacy pack) then pick one at random each time you say the line. One ★ alone adds nothing.'
        : ' The patch with this line exports its ★ takes: two or more play at random (DBVO 2), one ★ is used as the line\'s file in place of the take in use.')));
    drawnKey = key;
    insp.scrollTop = scrollTop;
  }

  // ---- Variant settings
  let rows = st.rows.map((r) => ({ ...r }));
  let ctx = st.tagsAsContext;
  let ctxDefault = st.contextIsDefault;
  const prefix = h('input', { type: 'text', value: st.prefix, placeholder: 'the line\'s own tags', style: { width: '330px' }, title: 'Sent in front of every variant made here, instead of the line\'s own leading tags. Saved for this voice pack\'s variants only.' });
  const presetSel = h('select', { title: 'Variant rows to start from' });
  const ctxBox = h('span.row', { style: { gap: '8px' } });
  const rgrid = h('div.var-rows');
  const save = debounce(async () => {
    try { await rpc('variants.saveSettings', { voiceId, prefix: prefix.value, rows, tagsAsContext: ctxDefault ? null : ctx, preset: presetSel.value }); } catch (e) { fail(e); }
  }, 500);
  const saveNow = async () => { await rpc('variants.saveSettings', { voiceId, prefix: prefix.value, rows, tagsAsContext: ctxDefault ? null : ctx, preset: presetSel.value }); };

  const renderPresets = () => {
    clear(presetSel).append(st.presets.map((p) => h('option', { value: p.name }, p.name + (p.builtIn ? '' : ' (yours)'))));
    if (st.preset && !st.presets.some((p) => p.name === st.preset)) presetSel.append(h('option', { value: st.preset }, st.preset));
    presetSel.value = st.preset || '';
    const mine = st.presets.find((p) => p.name === st.preset && !p.builtIn);
    delPreset.style.display = mine ? '' : 'none';
  };
  const delPreset = h('button.btn.sm.ghost', { title: 'Delete this preset of yours', onclick: async () => {
    const name = st.preset;
    if (!(await confirmBox('Delete preset?', `Delete your preset "${name}"? The rows stay as they are.`, 'Delete', true))) return;
    try { await rpc('variants.saveSettings', { voiceId, deletePreset: name }); const r = await rpc('variants.get', { voiceId }); data = r.data; Object.assign(st, data.settings); st.preset = 'Custom'; renderPresets(); save(); } catch (e) { fail(e); }
  } }, 'Delete preset');
  presetSel.addEventListener('change', () => {
    const p = st.presets.find((x) => x.name === presetSel.value);
    if (p) { rows = p.rows.map((r) => ({ ...r })); st.preset = p.name; renderRows(); renderStatus(); renderPreview(); save(); renderSummary(); }
  });

  const renderCtx = () => {
    const on = ctx && ctxAvail;
    clear(ctxBox).append(
      h('span.badge' + (on ? '.b-valid' : '.b-toolong'), { title: on
        ? 'The prefix tags go to ElevenLabs as context (previous_text): not billed, and a little weaker than in the text. Strong words (Extremely) make up for it.'
        : 'The prefix tags go in the text: billed as characters, at full strength.' }, on ? 'Context on' : 'Context off'),
      switchEl(on, (v) => { ctx = v; ctxDefault = false; renderCtx(); renderStatus(); save(); }, 'Send the prefix as context', { disabled: !ctxAvail, title: ctxAvail ? 'On: tags as context (not billed, a little weaker). Off: tags in the text (billed, full strength).' : (eleven ? 'eleven_v3 takes no context: the prefix goes in the text.' : 'Only ElevenLabs takes context: the prefix goes in the text.') }),
      h('span.faint', { style: { fontSize: '12px' } }, ctxAvail && ctxDefault ? `(your default from Global Settings: ${st.appContextDefault ? 'on' : 'off'})` : ''),
      ctxAvail && !ctxDefault ? h('button.btn.sm.ghost', { title: 'Follow the default in Global Settings again', onclick: () => { ctxDefault = true; ctx = st.appContextDefault; renderCtx(); renderStatus(); save(); } }, 'Use default') : null);
  };

  function renderRows() {
    clear(rgrid).append(['#', 'Stability', 'Speed', 'Variation word (merged into the prefix)', 'Seed', 'Sent as', ''].map((t) => h('div.rh', t)));
    rows.forEach((r, i) => {
      const stab = voice.discrete || !eleven
        ? h('select', { disabled: !eleven, title: "Creative: livelier, may drift (its takes are marked listen until you play them). Natural: the usual. Robust: steady, flatter." }, STAB.map(([v, n]) => h('option', { value: v }, n)))
        : h('input', { type: 'number', min: 0, max: 1, step: 0.05, style: { width: '84px' } });
      stab.value = voice.discrete || !eleven ? String(r.stability < 0.25 ? 0 : r.stability < 0.75 ? 0.5 : 1) : String(r.stability);
      stab.addEventListener('change', () => { r.stability = Number(stab.value); markCustom(); renderPreview(); renderRows(); });
      const speed = h('input', { type: 'number', min: 0.7, max: 1.2, step: 0.05, value: Number(r.speed).toFixed(2), style: { width: '76px' }, title: 'Pace: 0.70 to 1.20. Small steps (±0.05) sound natural.' });
      speed.addEventListener('change', () => { r.speed = Math.min(1.2, Math.max(0.7, Number(speed.value) || 1)); speed.value = r.speed.toFixed(2); markCustom(); });
      const word = h('input', { type: 'text', value: r.word || '', placeholder: 'none', style: { width: '100%' }, title: 'Merged into the prefix tag: [British Extremely Baleful, softly]. One tag, not two: two leading tags weaken each other.' });
      word.addEventListener('input', () => { r.word = word.value; markCustom(); renderPreview(); });
      const seedSel = h('select', { title: 'New: a different reading every time. Fixed: the same seed again, to repeat a reading you liked.' }, h('option', { value: 'new' }, 'New'), h('option', { value: 'fixed' }, r.fixedSeed && r.seed != null ? `Fixed: ${r.seed}` : 'Fixed…'));
      seedSel.value = r.fixedSeed ? 'fixed' : 'new';
      seedSel.addEventListener('change', async () => {
        if (seedSel.value === 'fixed') {
          const v = await modal('Fixed seed', [h('div', 'The same seed, text and settings give the same reading again (as far as ElevenLabs manages). Copy a seed from a take you liked, in the panel on the right.'), h('input#seedIn', { type: 'number', min: 0, max: 4294967295, value: r.seed ?? '' })], [{ label: 'Cancel', value: false }, { label: 'OK', primary: true, value: true }]);
          const raw = (document.getElementById('seedIn')?.value || '').trim();
          const n = Number(raw);
          // empty box: the app picks a seed and keeps it
          if (v && raw !== '' && Number.isFinite(n) && n >= 0) { r.fixedSeed = true; r.seed = Math.min(4294967295, Math.floor(n)); } else if (v) { r.fixedSeed = true; r.seed = 1 + Math.floor(Math.random() * 4294967294); }
        } else { r.fixedSeed = false; }
        markCustom(); renderRows();
      });
      const sent = h('div.mono.ellipsis.dim', { 'data-row': i });
      const del = h('button.act-btn', { title: 'Remove this variant row', disabled: rows.length <= 1, onclick: () => { rows.splice(i, 1); markCustom(); renderRows(); renderStatus(); } }, '✕');
      rgrid.append(h('div.dim', String(i + 1)), stab, speed, word, seedSel, sent, del);
    });
    renderPreview();
  }
  const markCustom = () => { st.preset = 'Custom'; renderPresets(); save(); renderStatus(); renderSummary(); };
  function renderPreview() {
    const l = (focusKey && data.lines.find((x) => x.key === focusKey)) || data.lines.find((x) => grid.selected.has(x.key)) || null;
    const spoken = lineData && lineData.key === focusKey ? lineData.spoken : l ? fullOf(l) : 'Falkreath';
    rgrid.querySelectorAll('[data-row]').forEach((el) => { const r = rows[Number(el.dataset.row)]; if (r) { el.textContent = variantText(spoken, prefix.value, r.word); el.title = el.textContent; } });
  }
  prefix.addEventListener('input', () => { renderPreview(); renderStatus(); save(); renderSummary(); });

  const addRow = h('button.btn.sm', { title: 'Another variant per line (ten at most)', onclick: () => { if (rows.length >= 10) return; rows.push({ ...rows[rows.length - 1], fixedSeed: false, seed: null }); markCustom(); renderRows(); renderStatus(); } }, '+ Variant');
  const savePreset = h('button.btn.sm.ghost', { title: 'Keep these rows under a name of your own', onclick: async () => {
    const got = await prompt('Save as preset', [{ name: 'name', label: 'Name for these variant rows', value: '' }]);
    const name = ((got && got.name) || '').trim();
    if (!name) return;
    let replace = false;
    if (st.presets.some((p) => !p.builtIn && p.name.toLowerCase() === name.toLowerCase())) {
      if (!(await confirmBox('Replace preset?', `You already have a preset called "${name}". Replace its rows with these?`, 'Replace'))) return;
      replace = true;
    }
    try { await rpc('variants.saveSettings', { voiceId, rows, savePreset: name, replace }); const r = await rpc('variants.get', { voiceId }); data = r.data; Object.assign(st, data.settings); renderPresets(); toast(`Saved as "${name}".`); } catch (e) { fail(e); }
  } }, 'Save as preset…');
  // Folded: one line saying what the rows are, so the line list keeps the room. Remembered per voice pack.
  let folded = !!store.get('variants-folded:' + voiceId, false);
  const foldBtn = h('button.btn.sm.ghost', { title: 'Fold the Variant settings to one line, or open them' });
  const summary = h('span.dim.ellipsis', { style: { fontSize: '12.5px', minWidth: 0 } });
  const renderSummary = () => {
    const p = normalizePrefix(prefix.value);
    summary.textContent = `${rows.length} variant${rows.length === 1 ? '' : 's'} per line: ${rows.map((r) => (eleven ? stabName(r.stability) : '') + (r.word ? (eleven ? ' ' : '') + r.word : '')).map((x) => x.trim() || 'as is').join(' · ')}${p ? ' · prefix ' + p : ''}`;
  };
  const body = h('div.var-recipe-body',
    h('div.row', { style: { gap: '10px', marginTop: '8px', flexWrap: 'wrap' } }, h('span.field-label', { style: { margin: 0, width: '110px' } }, 'Prefix [tags]'), prefix, ctxBox),
    eleven ? null : h('div.banner.warn', { style: { margin: '6px 0 0' } }, `This voice pack uses ${voice.engine}: Stability, Speed and Seed only apply to ElevenLabs, so variants here differ mostly by chance. The prefix and variation word only make a difference where the engine reads [tags].`),
    rgrid,
    h('div.row', { style: { gap: '10px', marginTop: '6px' } }, addRow, eleven ? [h('span.badge.b-toolong', 'Creative'), h('span.faint', { style: { fontSize: '12px' } }, "takes are marked listen until you play them, and get no ★ unless you give one (they can drift out of the voice's accent)")] : null));
  const recipe = h('div.var-recipe',
    h('div.row', { style: { gap: '12px', flexWrap: 'nowrap', minWidth: 0 } }, foldBtn, h('b', { style: { whiteSpace: 'nowrap' } }, 'Variant settings'), summary,
      h('div.spacer'), h('span.dim', 'Preset'), presetSel, delPreset, savePreset),
    body);
  const renderFold = () => {
    foldBtn.textContent = folded ? '▸' : '▾';
    body.style.display = folded ? 'none' : '';
    renderSummary();
  };
  foldBtn.addEventListener('click', () => { folded = !folded; store.set('variants-folded:' + voiceId, folded); renderFold(); });
  renderFold();
  view.append(recipe);

  // ---- status bar
  const status = h('div.statusbar');
  view.append(status);
  const genBtn = h('button.btn.primary', { onclick: () => generate([...grid.selected]) }, 'Generate variants');
  function renderStatus() {
    const picked = data.lines.filter((l) => grid.selected.has(l.key));
    let chars = 0;
    for (const l of picked) for (const r of rows) chars += billed(variantText(fullOf(l), prefix.value, r.word), ctx && ctxAvail);
    const est = eleven ? (rate ? `≈ ${chars.toLocaleString()} characters · ≈ ${Math.ceil(chars * rate).toLocaleString()} credits` : `≈ ${chars.toLocaleString()} characters`) : `${(picked.length * rows.length).toLocaleString()} takes`;
    genBtn.disabled = !picked.length || !!app.job;
    genBtn.title = !picked.length ? 'Click a line first' : app.job ? 'Another task is running' : `Make ${rows.length} variant take(s) for ${picked.length === 1 ? 'the selected line' : 'each of the ' + picked.length + ' selected lines'}`;
    clear(status).append(h('span.stat', { title: picked.map((l) => l.text).join('\n') }, picked.length === 1 ? ['Selected: ', h('b', picked[0].text)] : [h('b', picked.length.toLocaleString()), ` line${picked.length === 1 ? '' : 's'} selected`]),
      h('span.dim', `· ${rows.length} variant${rows.length === 1 ? '' : 's'} per line`),
      h('div.spacer'), h('span.dim', est), genBtn);
  }
  grid.el.addEventListener('selection', () => { renderStatus(); renderPreview(); });

  async function generate(keys, row = null) {
    if (!keys.length) { toast('Click a line first.', true); return; }
    const n = keys.length * (row == null ? rows.length : 1);
    const use = row == null ? rows : [rows[row]];
    let chars = 0;
    for (const k of keys) { const l = data.lines.find((x) => x.key === k); if (l) for (const r of use) chars += billed(variantText(fullOf(l), prefix.value, r.word), ctx && ctxAvail); }
    const cost = eleven ? ` About ${chars.toLocaleString()} characters${rate ? ` (≈ ${Math.ceil(chars * rate).toLocaleString()} credits)` : ''}.` : '';
    const names = keys.map((k) => (data.lines.find((x) => x.key === k) || {}).text || k);
    const listed = names.slice(0, 8).map((t) => `"${t}"`).join(', ') + (names.length > 8 ? ` and ${names.length - 8} more` : '');
    if (!(await confirmBox('Generate variants?', `${n.toLocaleString()} new take(s) for ${keys.length === 1 ? 'this line' : keys.length.toLocaleString() + ' lines'}: ${listed}.${cost} Context is ${ctx && ctxAvail ? 'on' : 'off'}.`, 'Generate'))) return;
    try { await saveNow(); await rpc('variants.generate', { voiceId, keys, row }); } catch (e) { fail(e); }
  }

  // ---- Add a line
  addBtn.addEventListener('click', async () => {
    const input = h('input', { type: 'text', placeholder: 'The line as it appears in game', style: { width: '100%' } });
    const spoken = h('input', { type: 'text', placeholder: 'Spoken text (tags allowed): empty = the line itself', style: { width: '100%' } });
    const sugg = h('div.var-sugg');
    const info = h('div');
    let check = null;
    const runCheck = debounce(async () => {
      const text = input.value;
      try {
        const [c, found] = await Promise.all([rpc('variants.checkLine', { voiceId, text }), rpc('variants.search', { voiceId, q: text })]);
        if (text !== input.value) return;
        check = c;
        clear(sugg).append(found.length ? [h('span.faint', 'Matches as you type: '), ...found.map((f) => h('a', { title: f.where, onclick: () => { input.value = f.text; runCheck(); } }, f.text))] : null);
        clear(info);
        if (!text.trim()) return;
        info.append(h('div.var-kv', h('div', 'File name'), h('div.mono', c.stem + '.fuz'),
          ...c.files.slice(1).map((f, i) => [h('div', i === 0 ? 'DBVO 2 variants' : 'DBReV L variants'), h('div.mono', f)]),
          h('div', 'Line check'), h('div', c.ok ? h('span.badge.b-valid', 'Valid') : h('span.badge.b-broken', 'Not voiceable'), h('span.faint', ' same rules as Phase 1'))));
        if (c.problems.length) info.append(h('div.help.bad', h('ul', c.problems.map((p) => h('li', p)))));
        if (c.existing.length) info.append(h('div.help.warn', h('p', h('b', 'Already voiced: '), `this line is in ${c.existing.join(', ')}. Its variants go there, so every patch with it shares them.`)));
      } catch (e) { fail(e); }
    }, 300);
    input.addEventListener('input', runCheck);
    const v = await modal('Add a line', [h('div.field-label', 'Line as it appears in game'), input, sugg, info, h('div.field-label', 'Spoken text'), spoken,
      h('div.faint', 'A line you add here only plays if its wording matches the game exactly. Pick it from the matches when you can.')],
      [{ label: 'Cancel', value: false }, { label: 'Add', value: 'add' }, { label: 'Add and generate', primary: true, value: 'gen' }], { onOpen: () => input.focus() });
    if (!v) return;
    try {
      const r = await rpc('variants.addLine', { voiceId, text: input.value, spoken: spoken.value });
      focusKey = r.key;
      await reload();
      // the current filter may hide it (Suggested leaves added lines out): show it under All lines
      if (!shown().some((l) => l.key === r.key)) { filter = 'all'; store.set('variants-filter:' + voiceId, filter); q.value = ''; renderCounts(); grid.setRows(shown()); }
      grid.selected = new Set([String(r.key)]); grid.paint(true); grid.el.dispatchEvent(new CustomEvent('selection'));
      grid.focus(r.key, true); renderInspector();
      if (r.existed) toast('This voice pack already has that line: it is selected in the list.');
      if (v === 'gen') await generate([r.key]);
    } catch (e) { fail(e); }
  });

  // ---- Variants Pack
  packBtn.addEventListener('click', () => popupMenu(packBtn, [
    { label: 'Build the Variants Pack', sub: `${data.lines.filter(hasVariants).length} line(s) → ${st.packName}.bsa + .esp in ${st.outputFolder || '(no folder set)'}${st.lastExport ? ` · last built ${new Date(st.lastExport).toLocaleString()}; build again after changing ★` : ' · not built yet'}`, disabled: !!app.job, onclick: async () => {
      if (!st.outputFolder) { toast('Set the folder first: Variants Pack ▾ → Name and folder.', true); return; }
      if (!(await confirmBox('Build the Variants Pack?', `Every line with two or more ★ takes goes into ${st.packName}.bsa in ${st.outputFolder}, as DBVO 2 numbered files and DBReV L name(1) files. An earlier Variants Pack with that name is replaced.`, 'Build'))) return;
      try { await saveNow(); await rpc('variants.export', { voiceId }); } catch (e) { fail(e); }
    } },
    { label: 'Add to a FOMOD project…', sub: 'A pre-ticked option with the BSA and its plugin, which users can untick', onclick: addToFomod },
    { label: 'Name and folder…', sub: `${st.packName} · ${st.outputFolder || 'not set'}`, onclick: async () => {
      // Empty fields follow the voice pack: the placeholders show what that gives now.
      const name = h('input', { type: 'text', value: st.packNameSet || '', placeholder: st.packName, style: { width: '100%' } });
      const folder = h('input', { type: 'text', value: st.outputFolderSet || '', placeholder: st.outputFolder || 'the voice pack has no release folder', style: { width: '100%' } });
      const pick = h('button.btn.sm', { onclick: async () => { try { const f = await rpc('pick.folder', { title: 'Folder for the Variants Pack' }); if (f) folder.value = f; } catch (e) { fail(e); } } }, 'Browse…');
      const ok = await modal('Variants Pack name and folder', [h('div.field-label', 'Name (the .bsa and .esp)'), name, h('div.field-label', 'Folder'), h('div.row', folder, pick),
        h('div.faint', 'Empty name: named after the voice pack. Empty folder: "Variants Pack" in the voice pack\'s release folder. Renaming later: Add to a FOMOD project again and its option follows; build again for the new files.')], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }]);
      if (!ok) return;
      try { await rpc('variants.saveSettings', { voiceId, packName: name.value, outputFolder: folder.value }); await reload(); } catch (e) { fail(e); }
    } },
    { label: 'Remove the Variants Pack…', sub: 'Deletes its BSA, plugin and lines list, and its option in your FOMOD projects. ★ takes stay.', disabled: !!app.job, onclick: async () => {
      if (!(await confirmBox('Remove the Variants Pack?', `${st.packName}.bsa, ${st.packName}.esp and its lines list are deleted from ${st.outputFolder || 'its folder'} (and the last pack you built, if it had another name or folder), and its option is taken out of this voice pack's FOMOD projects. Your ★ takes stay, so Build makes it again. This can't be undone.`, 'Remove', true))) return;
      try {
        const r = await rpc('variants.removePack', { voiceId });
        toast(`${r.files.length ? 'Deleted ' + r.files.join(', ') + '.' : 'No Variants Pack files were there.'}${r.fomods.length ? ' Option taken out of ' + r.fomods.join(', ') + '.' : ''}${r.locked && r.locked.length ? ' Still there (open in another program, close it and remove again): ' + r.locked.join(', ') : ''}`, !!(r.locked && r.locked.length));
        await reload();
      } catch (e) { fail(e); }
    } },
  ]));
  async function addToFomod() {
    try {
      const list = await rpc('variants.fomods', { voiceId });
      if (!list.length) { toast('This voice pack has no FOMOD project yet (Phase 3).', true); return; }
      const pickOne = list.length === 1 ? list[0] : await new Promise((resolve) => popupMenu(packBtn, list.map((f) => ({ label: f.name, onclick: () => resolve(f) }))));
      if (!pickOne) return;
      const r = await rpc('variants.addToFomod', { voiceId, fomodId: pickOne.id });
      toast(r.added ? `"${r.option}" is now a pre-ticked option in ${r.fomod} (group Variants Pack on the first page).` : `"${r.option}" in ${r.fomod} now points at the current Variants Pack.`);
    } catch (e) { fail(e); }
  }

  // ---- data
  async function reload() {
    try { const r = await rpc('variants.get', { voiceId }); data = r.data; Object.assign(st, data.settings); } catch (e) { fail(e); return; }
    renderCounts(); grid.setRows(shown()); renderStatus(); renderInspector();
  }
  q.addEventListener('input', debounce(() => grid.setRows(shown()), 200));
  sortSel.addEventListener('change', () => {
    sortBy = sortSel.value; store.set('variants-sort:' + voiceId, sortBy);
    if (grid.sortId) { grid.sortId = null; grid.sortDir = 0; store.set('sort:variants:' + voiceId, null); grid.renderHead(); }
    grid.setRows(shown());
  });
  const onDone = (e) => { if (!view.isConnected) return; if (e.detail && (e.detail.kind === 'variants' || e.detail.kind === 'variantsPack') && e.detail.voiceId === voiceId) reload(); else renderStatus(); };
  const onProgress = () => { if (view.isConnected) genBtn.disabled = true; };
  document.addEventListener('job-done', onDone);
  document.addEventListener('job-progress', onProgress);
  // Leaving the page: its listeners go and its sound stops (one set per visit, not piling up).
  const watch = setInterval(() => {
    if (view.isConnected) return;
    clearInterval(watch);
    document.removeEventListener('job-done', onDone);
    document.removeEventListener('job-progress', onProgress);
    audio.onended = null; audio.pause();
  }, 1000);

  renderPresets(); renderCtx(); renderRows(); renderCounts(); grid.setRows(shown()); renderStatus(); renderInspector();
  return view;
}
