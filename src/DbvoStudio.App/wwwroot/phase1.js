// Phase 1: Mods list and the per-plugin workspace.
import { findReplaceDialog } from './tagtools.js';
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, droppedPaths, debounce, popupMenu, columnsMenu, switchEl, askFixOverwrite, icon, helpButton, segmented, pathText, pathRows, pathList, searchLamp, closeDialogOf } from './lib.js';
import { app, go, refreshState } from './app.js';

const KIND_LABEL = {
  Valid: ['Valid', 'b-valid'],
  Fixed: ['Fixed', 'b-fixed'],
  Broken: ['BROKEN LINE', 'b-broken'],
  TooLong: ['TOO LONG WILL CTD', 'b-toolong'],
  SkippedEditorId: ['= EditorID', 'b-skip'],
};

export function kindBadge(l) {
  if (l.status === 'Removed') return h('span.badge.b-removed', 'Removed');
  if (l.needsDecision) return h('span.badge.b-toolong', { title: 'A stage direction: decide whether it is voiced (e.g. a laugh) or stays silent.' }, 'VOICE OR SILENT?');
  if (l.resolved && l.stage) return h('span.badge.b-fixed', { title: `Was ${(KIND_LABEL[l.kind] || [l.kind])[0]}; now a stage direction you ${l.voiced ? 'voice' : 'keep silent'}.` }, l.voiced ? 'Fixed · voiced' : 'Fixed · silent');
  if (l.resolved) return h('span.badge.b-fixed', { title: `Was ${(KIND_LABEL[l.kind] || [l.kind])[0]}; the final text now passes the script.` }, 'Fixed ✓');
  if ((l.variants || []).length && !l.isPlayerName && (l.kind === 'Broken' || l.kind === 'TooLong')) return h('span.badge.b-fixed', { title: `Was ${(KIND_LABEL[l.kind] || [l.kind])[0]}; the line stays as the mod wrote it and its ${l.variants.length} iteration(s) go into the topic file.` }, 'ITERATIONS ADDED');
  if (l.isPlayerName && l.kind !== 'TooLong') return h('span.badge.b-player', 'PLAYER NAME');
  const [t, c] = KIND_LABEL[l.kind] || [l.kind, 'b-skip'];
  return h('span.badge.' + c, t);
}

function categorySelect(value, onchange) {
  const cats = app.state.settings.categories;
  const sel = h('select', cats.map((c) => h('option', { value: c, selected: c === value }, c)), h('option', { value: '__new' }, '+ New category…'));
  if (value && !cats.includes(value)) sel.prepend(h('option', { value, selected: true }, value));
  sel.addEventListener('change', async () => {
    if (sel.value === '__new') {
      const r = await prompt('New category', [{ name: 'n', label: 'Name', placeholder: 'e.g. Armor & Clothing' }]);
      if (!r || !r.n.trim()) { sel.value = value; return; }
      const n = r.n.trim();
      // Keep it in the category list (Global Settings), so it can be picked for other mods too.
      if (!cats.includes(n)) {
        try { await rpc('categories.save', { categories: [...cats, n] }); cats.push(n); } catch (e) { fail(e); }
      }
      if (![...sel.options].some((o) => o.value === n)) sel.insertBefore(h('option', { value: n }, n), sel.querySelector('option[value="__new"]'));
      sel.value = n; value = n;
      onchange(n);
    } else { value = sel.value; onchange(sel.value); }
  });
  return sel;
}

// ------------------------------------------------------------------ list

export async function modsView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'Mods'),
    helpButton('add-mods'),
    h('span.sub', 'Read plugins or topic files and make their lines DBVO-ready. Shared by every voice pack.'),
    h('div.spacer'),
    h('button.btn', { title: 'Add many plugins at once: give each its mod name and category, and every mod becomes its own card', onclick: () => batchAdd([]) }, '+ Add many mods…'),
    h('button.btn.primary', { title: 'Create a mod project and add its plugins', onclick: newMod }, '+ New mod')));
  const body = h('div.view-body');
  view.append(body);
  // Dropping plugins anywhere on the list opens the batch table.
  body.addEventListener('dragover', (e) => { e.preventDefault(); body.classList.add('drop-hint'); });
  body.addEventListener('dragleave', (e) => { if (!body.contains(e.relatedTarget)) body.classList.remove('drop-hint'); });
  body.addEventListener('drop', async (e) => {
    e.preventDefault(); body.classList.remove('drop-hint');
    try { batchAdd(await droppedPaths([...(e.dataTransfer.files || [])])); } catch (err) { fail(err); }
  });
  if (!s.mods.length) {
    body.append(h('div.empty', h('h2', 'No mods yet'),
      h('div', 'Create a mod, then drop its .esp / .esm files (or topic .txt files) into it. Or drop many plugins here to add many mods at once.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Create a mod project and add its plugins', onclick: newMod }, '+ New mod'))));
    return view;
  }
  const q = h('input', { type: 'text', placeholder: 'Filter mods…', title: 'Show only mods whose name contains this', style: { width: '260px', marginBottom: '12px' } });
  const listEl = h('div');
  const render = () => {
    const f = q.value.toLowerCase();
    clear(listEl);
    const cats = [...s.settings.categories, ...new Set(s.mods.map((m) => m.category).filter((c) => !s.settings.categories.includes(c)))];
    for (const cat of cats) {
      const mods = s.mods.filter((m) => (m.category || 'Misc') === cat && (!f || m.name.toLowerCase().includes(f)));
      if (!mods.length) continue;
      // reviewed mods shrink to chips; mods with flagged lines to review stay full cards
      const unrevOf = (m) => m.plugins.reduce((a, p) => a + p.unreviewed, 0);
      const linesOfMod = (m) => m.plugins.reduce((a, p) => a + p.topicLines, 0);
      const work = mods.filter((m) => unrevOf(m) || !m.plugins.length), done = mods.filter((m) => !unrevOf(m) && m.plugins.length);
      listEl.append(h('div.cat', h('div.cat-head', h('h3', cat), h('span.faint', `${mods.length} mod${mods.length === 1 ? '' : 's'} · ${mods.reduce((a, m) => a + linesOfMod(m), 0).toLocaleString()} topic lines`),
        h('div.spacer'), work.length ? h('span.cat-work', `${work.length} to review`) : h('span.cat-done', 'all reviewed ✓')),
        !work.length ? null : h('div.cards2', work.map((m) => {
          const unrev = m.plugins.reduce((a, p) => a + p.unreviewed, 0);
          const lines = m.plugins.reduce((a, p) => a + p.topicLines, 0);
          const fixes = m.plugins.reduce((a, p) => a + p.fixes, 0);
          return h('div.pcard', { onclick: () => go('mod', { id: m.id }) },
            h('div.row', h('span.title.ellipsis.grow', m.name, m.version ? h('span.faint', { style: { fontWeight: 400 } }, '  v' + m.version) : null),
              unrev ? h('span.badge.b-broken', `${unrev} flagged to review`) : h('span.badge.b-valid', 'reviewed')),
            h('div.meta.mono.ellipsis', m.plugins.map((p) => p.fileName).join(', ') || 'no plugins yet'),
            h('div.stat-row', h('span', h('b', lines), ' topic lines'),
              fixes ? h('span', h('b', fixes), ' in DBVO Fix') : h('span', 'no DBVO Fix needed'),
              m.plugins.some((p) => p.playerNames) ? h('span', h('b', m.plugins.reduce((a, p) => a + (p.playerNames || 0), 0)), ' player-name') : null));
        })),
        done.length ? h('div.pchips', { style: { padding: '4px 14px 12px' } }, done.map((m) => h('div.pchip', { onclick: () => go('mod', { id: m.id }), title: `${m.name}${m.version ? ' v' + m.version : ''} · ${m.plugins.map((p) => p.fileName).join(', ')} · reviewed. Click to open.` },
          h('span.pchip-name', m.name), h('span.mono.faint', linesOfMod(m).toLocaleString())))) : null));
    }
  };
  q.addEventListener('input', render);
  render();
  body.append(q, listEl);
  return view;
}

// ------------------------------------------------------------------ batch add

const PLUGIN_EXT = /\.(esp|esm|esl|txt)$/i;
const fileOf = (p) => p.split(/[\\/]/).pop();

/** A readable mod name from a plugin file name: "SeranaDialogAddon.esp" -> "Serana Dialog Addon". */
function nameFromFile(file) {
  let n = file.replace(PLUGIN_EXT, '').replace(/[_]+/g, ' ');
  if (!/\s/.test(n)) n = n.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  return n.replace(/\s+/g, ' ').trim();
}

/** Plugins whose name starts with another dropped plugin's name ("Mod - Patch.esp" after "Mod.esp") get that mod's name. */
function defaultRows(paths) {
  const files = paths.filter((p) => PLUGIN_EXT.test(p));
  const bases = files.map((p) => fileOf(p).replace(PLUGIN_EXT, ''));
  return files.map((p, i) => {
    const base = bases[i];
    const parent = bases.filter((b, j) => j !== i && b.length >= 3 && base.length > b.length && base.toLowerCase().startsWith(b.toLowerCase()) && /^[\s_\-.]/.test(base.slice(b.length)))
      .sort((a, b) => b.length - a.length)[0];
    return { path: p, file: fileOf(p), name: nameFromFile(parent ? parent + '.esp' : fileOf(p)), category: 'Misc' };
  });
}

async function batchAdd(initial) {
  const s = app.state;
  let rows = defaultRows(initial || []);
  const known = new Set(s.mods.map((m) => m.name.trim().toLowerCase()));
  const body = h('tbody');
  const summary = h('div.faint');
  const problems = h('div');
  const table = h('div.batch-wrap', h('table.tbl.batch', h('thead', h('tr', h('th', 'Plugin'), h('th', 'Mod name'), h('th', 'Category'), h('th', ''))), body));
  const drop = h('div.dropzone', { title: 'Drop .esp / .esm / .esl or topic .txt files' }, 'Drop more plugins here, or ', h('a', { href: '#', onclick: async (e) => { e.preventDefault(); add(await rpc('pick.files', { title: 'Choose plugins', filter: 'Plugins and topic files|*.esp;*.esm;*.esl;*.txt' }).catch(() => null)); } }, 'choose files…'));
  drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
  drop.addEventListener('dragleave', () => drop.classList.remove('over'));
  drop.addEventListener('drop', async (e) => { e.preventDefault(); e.stopPropagation(); drop.classList.remove('over'); try { add(await droppedPaths([...(e.dataTransfer.files || [])])); } catch (err) { fail(err); } });
  const paste = h('textarea', { rows: 4, placeholder: 'ExampleMod.esp | Example Mod | Quests\nExampleMod - Patch.esp | Example Mod | Quests', spellcheck: false });
  const pasteMsg = h('div.faint');

  function add(paths) {
    if (!paths || !paths.length) return;
    const have = new Set(rows.map((r) => r.path.toLowerCase()));
    const fresh = defaultRows([...rows.map((r) => r.path), ...paths.filter((p) => !have.has(p.toLowerCase()))]);
    // keep what was typed for rows already in the table
    rows = fresh.map((f) => rows.find((r) => r.path === f.path) || f);
    render();
  }

  function groups() {
    const g = new Map();
    for (const r of rows) {
      const key = r.name.trim().toLowerCase();
      if (!key) continue;
      if (!g.has(key)) g.set(key, { name: r.name.trim(), category: r.category, rows: [] });
      g.get(key).rows.push(r);
    }
    return [...g.values()];
  }

  function check() {
    const out = [];
    if (rows.some((r) => !r.name.trim())) out.push('Every plugin needs a mod name.');
    for (const g of groups()) {
      if (known.has(g.name.toLowerCase())) out.push(`"${g.name}" already exists. Add its plugins on that mod's page instead, or use another name.`);
      const cats = new Set(g.rows.map((r) => r.category));
      if (cats.size > 1) out.push(`"${g.name}" has plugins in different categories (${[...cats].join(', ')}): pick one.`);
      const files = g.rows.map((r) => r.file.toLowerCase());
      if (new Set(files).size !== files.length) out.push(`"${g.name}" has the same file twice.`);
    }
    return out;
  }

  function render() {
    clear(body);
    const g = groups();
    rows.forEach((r, i) => {
      const nameIn = h('input', { type: 'text', value: r.name, placeholder: 'Mod name' });
      nameIn.addEventListener('input', () => { r.name = nameIn.value; renderMeta(); });
      nameIn.addEventListener('change', render);
      const cat = categorySelect(r.category, (c) => {
        // the whole mod shares one category
        for (const x of rows) if (x.name.trim().toLowerCase() === r.name.trim().toLowerCase()) x.category = c;
        if (!app.state.settings.categories.includes(c)) app.state.settings.categories.push(c);
        render();
      });
      const grp = g.find((x) => x.rows.includes(r));
      const shared = grp && grp.rows.length > 1 && grp.rows[0] !== r;
      body.append(h('tr' + (shared ? '.joined' : ''),
        h('td.mono', { title: r.path }, r.file),
        h('td', nameIn, shared ? h('div.faint', { style: { fontSize: '11px' } }, 'same mod as ' + grp.rows[0].file) : null),
        h('td', cat),
        h('td', h('button.icon-btn', { title: 'Leave this plugin out', onclick: () => { rows.splice(i, 1); render(); } }, '✕'))));
    });
    if (!rows.length) body.append(h('tr', h('td', { colSpan: 4 }, h('div.faint', { style: { padding: '14px', textAlign: 'center' } }, 'No plugins yet.'))));
    renderMeta();
  }

  function renderMeta() {
    const g = groups();
    summary.textContent = rows.length ? `${rows.length} plugin(s) → ${g.length} mod(s). Plugins with the same mod name become one mod.` : '';
    const p = rows.length ? check() : [];
    clear(problems).append(...p.map((x) => h('div.banner.bad', { style: { margin: '4px 0 0' } }, x)));
  }

  function applyPaste() {
    const lines = paste.value.split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
    const bad = [];
    let applied = 0;
    for (const l of lines) {
      const parts = l.split('|').map((x) => x.trim());
      if (parts.length < 2 || !parts[0] || !parts[1]) { bad.push(`"${l}": use Plugin.esp | Mod Name | Category`); continue; }
      const r = rows.find((x) => x.file.toLowerCase() === parts[0].toLowerCase());
      if (!r) { bad.push(`${parts[0]}: not in the table (drop it first)`); continue; }
      r.name = parts[1];
      if (parts[2]) { r.category = parts[2]; if (!app.state.settings.categories.includes(parts[2])) app.state.settings.categories.push(parts[2]); }
      applied++;
    }
    pasteMsg.textContent = `${applied} line(s) applied.` + (bad.length ? ` ${bad.length} skipped: ` + bad.slice(0, 4).join('; ') + (bad.length > 4 ? '…' : '') : '');
    pasteMsg.style.color = bad.length ? 'var(--warn)' : '';
    render();
  }

  const status = h('div.faint');
  const createBtn = h('button.btn.primary', { title: 'Create one mod per row and read its plugins', onclick: create }, 'Create mods and import');
  async function create() {
    const p = check();
    if (!rows.length) { toast('Add plugins first.', true); return; }
    if (p.length) { toast(p[0], true); return; }
    createBtn.disabled = true;
    const g = groups();
    try { await rpc('backup.now', { reason: 'before-batch-add-mods' }); } catch { /* the import still runs */ }
    const results = [];
    for (let i = 0; i < g.length; i++) {
      const m = g[i];
      status.textContent = `Importing ${i + 1} / ${g.length}: ${m.name}…`;
      try {
        const mod = await rpc('mod.create', { name: m.name, category: m.category || 'Misc' });
        const r = await rpc('mod.import', { id: mod.id, paths: m.rows.map((x) => x.path), noBackup: true });
        const miss = [...new Set(r.reports.flatMap((x) => x.missingMasters || []))];
        const strings = r.reports.reduce((a, x) => a + (x.unresolvedStrings || 0), 0);
        results.push({ name: m.name, ok: true, plugins: m.rows.length, lines: r.reports.reduce((a, x) => a + x.added + x.kept + x.changed, 0), miss, strings });
      } catch (e) { results.push({ name: m.name, ok: false, error: e.message || String(e) }); }
    }
    closeDialogOf(createBtn);
    const bad = results.filter((r) => !r.ok);
    await modal('Mods added', [
      h('div', `${results.length - bad.length} of ${results.length} mod(s) created.`),
      h('div.batch-wrap', h('table.tbl', h('tbody', results.map((r) => h('tr',
        h('td', h('b', r.name)),
        r.ok ? h('td', `${r.plugins} plugin(s), ${r.lines.toLocaleString()} lines`) : h('td', { style: { color: 'var(--bad)' } }, r.error),
        h('td.faint', r.ok ? [r.miss.length ? 'Missing masters: ' + r.miss.join(', ') : '', r.strings ? ` ${r.strings} unresolved strings` : ''].join(' ').trim() || 'OK' : '')))))),
    ], [{ label: 'OK', primary: true, value: true }], { wide: true });
    go('mods');
  }

  render();
  await modal('Add many mods', [
    h('div.faint', 'One row per plugin. Give each a mod name and a category; plugins with the same mod name become one mod (e.g. a mod and its patch). Every mod becomes its own card, reviewed on its own page as usual.'),
    drop, table, summary, problems,
    h('details.batch-paste', h('summary', 'Paste names and categories'),
      h('div.faint', { style: { margin: '6px 0' } }, 'One line per plugin: ', h('span.mono', 'Plugin.esp | Mod Name | Category'), '. Order doesn\'t matter; the plugin file name finds the row. A new category is created.'),
      paste, h('div.row', { style: { marginTop: '6px' } }, h('button.btn.sm', { title: 'Turn the pasted list into rows above', onclick: applyPaste }, 'Apply list'), pasteMsg)),
    h('div.row', status, h('div.spacer'), createBtn),
  ], [{ label: 'Cancel', value: false }], { wide: true, help: 'add-mods' });
}

async function newMod() {
  const name = h('input', { type: 'text', placeholder: 'e.g. Legacy of the Dragonborn' });
  const version = h('input', { type: 'text', placeholder: 'e.g. 6.10.1' });
  let category = 'Misc';
  const cat = categorySelect(category, (c) => { category = c; if (![...cat.options].some((o) => o.value === c)) cat.prepend(h('option', { value: c, selected: true }, c)); cat.value = c; });
  const ok = await modal('New mod', [
    h('div.col', h('div.field-label', 'Mod name'), name),
    h('div.col', h('div.field-label', 'Mod version (optional)'), version),
    h('div.col', h('div.field-label', 'Category'), cat, h('div.faint', 'Sorts this mod\'s DBVO Patches (Followers, NSFW, …). Never used in folder paths.')),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }]);
  if (!ok || !name.value.trim()) return;
  try {
    const m = await rpc('mod.create', { name: name.value, version: version.value, category });
    go('mod', { id: m.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ workspace

export async function modView(id) {
  await refreshState();
  let mod = await rpc('mod.get', { id });
  const ui = store.get('mod:' + id, { plugin: null, filter: 'all' });
  let plugin = mod.plugins.find((p) => p.fileName === ui.plugin) || mod.plugins[0] || null;
  let filter = ui.filter || 'all';
  let search = '';
  let focusedKey = null;
  let showInsp = store.get('insp1', true);

  const view = h('div.view.work');
  const head = h('div.view-head');
  const hint = h('div.hint', h('b', 'Review flagged lines'), ' (BROKEN LINE, TOO LONG WILL CTD, PLAYER NAME), then ', h('b', 'Continue to Phase 2'), '. ',
    'A DBVO Fix plugin is only made when at least one line needs fixing; a plugin with no edited lines gets none. ',
    'Your edits are saved as you go. In Phase 2 they are written for you to the patch\'s DBVO Fix folder and its final folder, or use ', h('b', 'Export…'), ' (top right) to extract them anywhere now.');
  const banners = h('div');
  const tabs = h('div.filters');
  const filters = h('div.filters');
  const split = h('div.split' + (showInsp ? '' : '.no-insp'));
  const left = h('div.left');
  const insp = h('div.inspector');
  split.append(left, insp);
  const actions = h('div.statusbar');
  view.append(head, hint, banners, tabs, filters, split, actions);

  const grid = new VGrid(left, {
    storageKey: 'phase1',
    rowKey: (l) => l.key,
    rowClass: (l) => (l.excluded || l.status === 'Removed' || (l.hiddenByPrompts && !l.includeAnyway) || l.kind === 'SkippedEditorId' ? 'muted' : ''),
    onRowClick: (l) => { focusedKey = l.key; renderInspector(); },
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (l) => grid.checkbox(l) },
      { id: 'type', title: 'Status', width: 170, align: 'center', render: (l) => h('div.row', { style: { justifyContent: 'safe center' } }, kindBadge(l),
        l.status === 'ChangedInMod' ? h('span.badge.b-changed', 'changed in mod') : null,
        l.hiddenByPrompts && !l.includeAnyway ? h('span.badge.b-hidden', { title: 'Looks like an EditorID rather than a line the player says, so it starts out hidden and unvoiced. Click ♪ to voice it anyway.' }, 'hidden') : null) },
      { id: 'formid', title: plugin && plugin.isTopicFile ? '' : 'FormID', width: 90, cls: 'mono dim', render: (l) => l.formId },
      { id: 'orig', title: 'Original', flex: 1, width: 260, render: (l) => h('span.dim', { title: l.original }, l.original) },
      { id: 'final', title: 'Final in-game line', flex: 1.3, width: 320, render: (l) => finalCell(l) },
      { id: 'fix', title: 'Fix', tip: 'Edit goes into the DBVO Fix plugin', width: 56, resizable: false, align: 'center', render: (l) => approvalChip(l, 'fix') },
      { id: 'voice', title: 'Voice', tip: 'Voiced in Phase 2 (in the topic file)', width: 56, resizable: false, align: 'center', render: (l) => approvalChip(l, 'voice') },
      { id: 'excl', title: 'Exclude', tip: 'Leave the line out of the topic file', width: 66, resizable: false, align: 'center', render: (l) => h('button.act-btn.excl-btn' + (l.excluded ? '.on.bad' : ''), { title: l.excluded ? 'Excluded: click to include again' : 'Exclude from the topic file', onclick: () => act(l, 'exclude', !l.excluded) }, icon('ban', { size: 14 })) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  // Two independent approvals: voiced (topic file) and the edit in the DBVO Fix plugin.
  function approvalChip(l, which) {
    if (l.status === 'Removed' || l.excluded) return null;
    const chip = (on, explicit, against, label, title, onclick) => h('button.appr' + (on ? '.on' : '') + (against ? '.against' : '') + (explicit ? '.explicit' : ''), { title, onclick }, label);
    if (which === 'voice') return chip(l.voiced, l.voiceApproved != null, l.voiceApproved != null && l.voiceApproved !== l.defaultVoice,
      h('span.note', '♪'), (l.voiced ? 'Voiced: in the topic file and generated in Phase 2.' : 'Not voiced.') + (l.voiceApproved == null ? ' (default)' : ' (your choice)') + ' Click to switch.',
      () => act(l, 'voice', !l.voiced));
    if (plugin.isTopicFile) return null;
    const f = chip(l.inFix, l.fixApproved != null, l.fixApproved != null && l.fixApproved !== l.defaultFix,
      'Fix', !l.changed ? 'The text is unchanged, so nothing goes into the DBVO Fix plugin.' : (l.inFix ? 'Your edit goes into the DBVO Fix plugin (the subtitle changes in game).' : 'Your edit is NOT written to the DBVO Fix plugin.') + (l.fixApproved == null ? ' (default)' : ' (your choice)') + ' Click to switch.',
      () => { if (l.changed) act(l, 'fix', !l.inFix); });
    if (!l.changed) f.disabled = true;
    f.classList.add('fix');
    return f;
  }

  // Curly quotes, long dashes and the … character show as squares ("tofu") in Skyrim's subtitles: replace them as you type.
  function detofu(el) {
    el.addEventListener('input', () => {
      const v = el.value;
      if (!/[‘’“”—–…]/.test(v)) return;
      const pos = el.selectionStart;
      const fix = (t) => t.replace(/[‘’]/g, "'").replace(/[“”]/g, '"').replace(/—/g, '--').replace(/–/g, '-').replace(/…/g, '...');
      const before = fix(v.slice(0, pos));
      el.value = before + fix(v.slice(pos));
      el.setSelectionRange(before.length, before.length);
      if (!store.get('tofuNoticed', false)) { store.set('tofuNoticed', true); toast('Changed to plain punctuation so Skyrim\'s font can show it.'); }
    });
    return el;
  }

  function finalInput(l) {
    if (l.status === 'Removed') return h('span.faint', l.final || '');
    const aliasLine = l.hasOtherAlias && !l.isPlayerName;
    const inp = h('input', { type: 'text', value: l.final ?? '', placeholder: aliasLine ? ((l.variants || []).length ? 'Line left as is: its iterations are voiced' : 'Fix this line, or add iterations →') : l.flagged ? 'Fix this line, or leave empty' : '' });
    if (l.needsOverride) inp.style.color = 'var(--link)';
    inp.addEventListener('change', () => act(l, 'setFinal', inp.value));
    inp.addEventListener('focus', () => { if (focusedKey !== l.key) { focusedKey = l.key; renderInspector(); } });
    return detofu(inp);
  }

  // The final line, with the script's suggestion (✓) and undo (↶) next to it.
  function finalCell(l) {
    const inp = finalInput(l);
    const acc = l.suggested && l.final !== l.suggested ? h('button.icon-btn', { title: 'Use the script\'s suggestion: ' + l.suggested, onclick: () => act(l, 'accept') }, '✓') : null;
    const rev = l.final != null && l.final !== l.original && l.status !== 'Removed' ? h('button.icon-btn', { title: 'Undo: back to the original text', onclick: () => act(l, 'revert') }, '↶') : null;
    const n = (l.variants || []).length;
    const it = l.status !== 'Removed' && !l.isPlayerName && (n || l.hasOtherAlias)
      ? h('button.iter-chip' + (n ? '.on' : ''), { title: n ? `${n} iteration(s) go into the topic file:\n${l.variants.slice(0, 12).join('\n')}${n > 12 ? '\n…' : ''}\nClick to change them.` : 'Add iterations: the line written out every way it shows in game (each name or place the alias can be). The line itself stays as it is.', onclick: (e) => { e.stopPropagation(); iterationsDialog([l]); } }, n ? `${n} iter.` : '+ Iter.')
      : null;
    return acc || rev || it ? h('span.spoken-wrap', inp, acc, rev, it) : inp;
  }

  // ---- Iterations: write an <Alias=…> line out as every name or place it can show in game. Each becomes its own topic line.
  const ALIAS_RX = /<Alias(?:\.(\w+))?\s*=\s*([^>]+?)\s*>/gi;
  const aliasesOf = (t) => [...(t || '').matchAll(ALIAS_RX)].map((m) => m[2]).filter((a) => a.toLowerCase() !== 'player');
  const baseText = (l) => (l.final && aliasesOf(l.final).length ? l.final : l.original);
  function expand(text, values) {
    const names = [...new Set(aliasesOf(text).map((a) => a.toLowerCase()))];
    let out = [''];
    let combos = [{}];
    for (const n of names) {
      const vals = values[n] || [];
      if (!vals.length) return [];
      const next = [];
      for (const c of combos) for (const v of vals) { next.push({ ...c, [n]: v }); if (next.length > 500) break; }
      combos = next;
    }
    out = combos.map((c) => text.replace(ALIAS_RX, (m, mod, name) => {
      if (name.toLowerCase() === 'player') return m;
      const v = c[name.toLowerCase()] || m;
      return /^cap/i.test(mod || '') ? v.charAt(0).toUpperCase() + v.slice(1) : v;
    }).replace(/\s{2,}/g, ' ').trim());
    return [...new Set(out)];
  }
  async function iterationsDialog(lines) {
    lines = lines.filter((l) => l.status !== 'Removed' && !l.isPlayerName);
    if (!lines.length) { toast('No lines to add iterations to.', true); return; }
    const saved = mod.aliasValues || {};
    const names = [];
    for (const l of lines) for (const a of aliasesOf(baseText(l))) if (!names.some((x) => x.toLowerCase() === a.toLowerCase())) names.push(a);
    const inputs = {};
    const aliasRows = names.map((n) => {
      const key = Object.keys(saved).find((k) => k.toLowerCase() === n.toLowerCase());
      const inp = h('input', { type: 'text', value: key ? saved[key].join(', ') : '', placeholder: 'e.g. Whiterun, Markarth, Riften (comma between them)', style: { width: '100%' } });
      inp.addEventListener('input', update);
      inputs[n.toLowerCase()] = inp;
      return h('div.col', { style: { gap: '3px' } }, h('div.field-label.mono', { style: { textTransform: 'none', letterSpacing: 0 } }, `<Alias=${n}>`), inp);
    });
    const manual = h('textarea', { rows: lines.length === 1 ? 4 : 2, placeholder: lines.length === 1 ? 'Your own iterations, one per line (written exactly as the subtitle reads in game)' : 'Not available for several lines at once', disabled: lines.length !== 1, spellcheck: false },
      lines.length === 1 ? (lines[0].variants || []).join('\n') : '');
    manual.addEventListener('input', update);
    let replace = lines.length === 1;
    const modeSel = segmented([['add', 'Add to existing'], ['replace', 'Replace existing']], replace ? 'replace' : 'add', (v) => { replace = v === 'replace'; update(); });
    const sum = h('div.fr-sum'), prev = h('div.fr-preview');
    let result = [];
    function values() { const o = {}; for (const [k, inp] of Object.entries(inputs)) o[k] = inp.value.split(/[,\n]/).map((x) => x.trim()).filter(Boolean); return o; }
    function update() {
      const vals = values();
      result = lines.map((l) => {
        const gen = expand(baseText(l), vals);
        const own = lines.length === 1 ? manual.value.split('\n').map((x) => x.trim()).filter(Boolean) : [];
        const base = lines.length === 1 ? [] : (replace ? [] : (l.variants || []));
        return { l, v: [...new Set([...base, ...own, ...gen])] };
      });
      const total = result.reduce((a, r) => a + r.v.length, 0);
      const long = result.flatMap((r) => r.v).filter((x) => x.length > 150).length;
      const missing = names.filter((n) => !vals[n.toLowerCase()].length);
      sum.textContent = `${total.toLocaleString()} iteration(s) for ${lines.length.toLocaleString()} line(s)` + (missing.length && names.length ? ` · fill in ${missing.length} alias(es) to write the rest out` : '');
      clear(prev);
      const all = result.flatMap((r) => r.v);
      if (all.length > 300) prev.append(h('div.faint', `Showing the first 300 of ${all.length.toLocaleString()}.`));
      for (const r of result) for (const v of r.v) if (prev.childElementCount < 301) prev.append(h('div.fr-item', { style: v.length > 150 ? { borderColor: 'var(--bad)' } : null }, v, v.length > 150 ? h('span', { style: { color: 'var(--bad)' } }, `  · ${v.length} characters: over 150 crashes DBVO`) : null));
      if (long) prev.prepend(h('div.banner.bad', { style: { margin: 0 } }, `${long} iteration(s) are over 150 characters and would crash DBVO. Shorten those names, or fix the line itself.`));
    }
    const body = [
      h('div.help', h('p', 'The line stays as the mod wrote it. Each iteration goes into the topic file as its own line, so Phase 2 voices every way the subtitle can read in game. You can still edit the line itself as well.'),
        names.length ? h('p', 'Type what each alias can be. The values are remembered for this mod, so the next line with the same alias is filled in for you.') : null),
      lines.length > 1 ? h('div.faint', `${lines.length} lines: ${lines.slice(0, 3).map((l) => '"' + baseText(l) + '"').join(', ')}${lines.length > 3 ? '…' : ''}`) : h('div.box', baseText(lines[0])),
      ...aliasRows,
      lines.length === 1 ? h('div.col', { style: { gap: '3px' } }, h('div.field-label', 'Your own iterations'), manual) : h('div.row', h('span.faint', 'Existing iterations:'), modeSel),
      sum, prev,
    ];
    const p = modal(lines.length === 1 ? 'Iterations of this line' : 'Iterations of the alias lines', body, [{ label: 'Cancel', value: false }, { label: 'Save iterations', primary: true, value: true }], { help: 'fix-voice-and-exclude' });
    update();
    if (!(await p)) return;
    const av = {}; for (const n of names) av[n] = values()[n.toLowerCase()];
    try {
      mod = await rpc('line1.setIterations', { modId: mod.id, plugin: plugin.fileName, mode: lines.length === 1 || replace ? 'replace' : 'add', items: result.map((r) => ({ k: r.l.key, v: r.v })), aliasValues: av });
      plugin = mod.plugins.find((x) => x.fileName === plugin.fileName);
      renderAll();
      const total = result.reduce((a, r) => a + r.v.length, 0);
      toast(`${total.toLocaleString()} iteration(s) saved for ${result.length} line(s). They go into the topic file for Phase 2.`);
    } catch (e) { fail(e); }
  }

  async function act(l, action, value) {
    try {
      const r = await rpc('line1.update', { modId: mod.id, plugin: plugin.fileName, key: l.key, action, value });
      Object.assign(l, r.line);
      Object.assign(plugin, { ...r.plugin, lines: plugin.lines });
      grid.refreshRow(l.key, action !== 'review');
      if (focusedKey === l.key) renderInspector();
      renderTabs();
      renderActions();
    } catch (e) { fail(e); }
  }

  async function reload() {
    mod = await rpc('mod.get', { id });
    plugin = mod.plugins.find((p) => p.fileName === (plugin && plugin.fileName)) || mod.plugins[0] || null;
    renderAll();
  }

  const live = (l) => l.status !== 'Removed' && !l.excluded;
  function counts() {
    const L = plugin ? plugin.lines : [];
    return {
      all: L.length,
      fixes: L.filter((l) => l.needsOverride).length,
      notvoiced: L.filter((l) => !l.voiced && l.status !== 'Removed' && !l.excluded && l.kind !== 'SkippedEditorId').length,
      stage: L.filter((l) => l.stage && live(l)).length,
      flagged: L.filter((l) => l.flagged && live(l)).length,
      unreviewed: L.filter((l) => l.flagged && !l.reviewed && live(l)).length,
      player: L.filter((l) => l.isPlayerName && live(l)).length,
      changed: L.filter((l) => l.status === 'ChangedInMod').length,
      edited: L.filter((l) => l.userEdited).length,
      alias: L.filter((l) => l.hasOtherAlias && !l.isPlayerName && live(l)).length,
      iter: L.filter((l) => (l.variants || []).length).length,
      hidden: L.filter((l) => l.hiddenByPrompts && !l.includeAnyway).length,
      excluded: L.filter((l) => l.excluded).length,
      removed: L.filter((l) => l.status === 'Removed').length,
    };
  }

  function visibleLines() {
    if (!plugin) return [];
    const q = search.toLowerCase();
    return plugin.lines.filter((l) => {
      switch (filter) {
        case 'fixes': if (!l.needsOverride) return false; break;
        case 'notvoiced': if (!(!l.voiced && l.status !== 'Removed' && !l.excluded && l.kind !== 'SkippedEditorId')) return false; break;
        case 'stage': if (!(l.stage && live(l))) return false; break;
        case 'flagged': if (!(l.flagged && live(l))) return false; break;
        case 'unreviewed': if (!(l.flagged && !l.reviewed && live(l))) return false; break;
        case 'player': if (!(l.isPlayerName && live(l))) return false; break;
        case 'changed': if (l.status !== 'ChangedInMod') return false; break;
        case 'hidden': if (!(l.hiddenByPrompts && !l.includeAnyway)) return false; break;
        case 'excluded': if (!l.excluded) return false; break;
        case 'removed': if (l.status !== 'Removed') return false; break;
        case 'edited': if (!l.userEdited) return false; break;
        case 'alias': if (!(l.hasOtherAlias && !l.isPlayerName && live(l))) return false; break;
        case 'iter': if (!(l.variants || []).length) return false; break;
      }
      return !q || (l.original || '').toLowerCase().includes(q) || (l.final || '').toLowerCase().includes(q) || l.formId.toLowerCase().includes(q) || (l.editorId || '').toLowerCase().includes(q);
    });
  }

  function renderHead() {
    const addBtn = h('button.btn', { title: 'Add plugins or topic files to this mod' }, 'Add plugins or topic files ▾');
    addBtn.addEventListener('click', () => popupMenu(addBtn, [
      { label: 'Plugins (.esp / .esm / .esl)…', sub: 'Added to this mod for DBVO patching. A newer version of a plugin already here updates it and keeps your edits.', onclick: () => addFiles('plugins') },
      { label: 'Topic files (.txt)…', sub: 'For mods without a plugin (old mods, player-name collections). Lines are checked the same way; no DBVO Fix plugin.', onclick: () => addFiles('topics') },
    ]));
    const exportBtn = h('button.btn', { disabled: !plugin, title: 'Export topic files, DBVO Fix plugins or player-name lines' }, 'Export… ▾');
    exportBtn.addEventListener('click', () => popupMenu(exportBtn, [
      { label: 'Topic files + DBVO Fix plugins to a folder…', sub: 'Only for using them outside this app. Phase 2 writes them for you.', onclick: exportOutputs },
    ]));
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the list of mods', onclick: () => go('mods') }, '← Mods'),
      h('h1', mod.name),
      helpButton('add-or-update-files-in-a-mod'),
      mod.version ? h('span.sub', 'v' + mod.version) : null,
      h('button.icon-btn', { title: 'Rename / version', onclick: editMeta }, '✎'),
      h('span.faint', 'Mod Category'),
      h('span', { title: 'The category this mod and its patches are listed under' }, categorySelect(mod.category, async (c) => { try { await rpc('mod.setCategory', { id, category: c }); mod.category = c; await refreshState(); renderHead(); } catch (e) { fail(e); } })),
      h('div.spacer'),
      addBtn,
      exportBtn,
      h('button.btn.ghost.danger', { title: 'Delete this mod project (a backup is made first)', onclick: delMod }, 'Delete'));
  }

  function renderBanners() {
    clear(banners);
    if (!mod.plugins.length) {
      const dz = h('div.dropzone', { style: { margin: '18px' } },
        h('div', 'Drop .esp / .esm / .esl plugins here, or ', h('a', { href: '#', onclick: (e) => { e.preventDefault(); addFiles(); } }, 'choose files'), '.'),
        h('div.faint', { style: { marginTop: '6px' } }, 'No plugin? Drop a topic .txt instead (for old mods that won\'t update, or collections such as player-name lines). Its lines are checked the same way, but no DBVO Fix plugin can be made from a topic file.'));
      wireDrop(dz, importPaths);
      banners.append(dz);
      return;
    }
    if (!plugin) return;
    if (plugin.isTopicFile) {
      banners.append(h('div.banner.info', h('span', h('b', 'Topic file. '), 'Lines are checked with the script\'s rules but kept as written: they should match the game\'s subtitles, and changing a line here changes its filename without changing the game. No DBVO Fix plugin is made.')));
    }
    if (plugin.missingMasters.length) {
      banners.append(h('div.banner.warn',
        h('b', 'Missing masters:'),
        plugin.missingMasters.map((m) => h('span.row', h('span.mono', m.fileName),
          m.nexusSearchUrl ? h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url: m.nexusSearchUrl }); } }, 'find on Nexus') : h('span.faint', '(set the base game folder in Global Settings → General & tools → Plugins)'))),
        h('div.spacer'),
        h('button.btn.sm', { title: 'Upload the master files this plugin needs so its lines can be read', onclick: uploadMasters }, 'Upload masters…')));
    }
    for (const n of plugin.notices) {
      // Information (texts re-read from the game's own strings) is not a missing-strings problem: shown in blue, dismissable.
      if (/game's own wording/.test(n)) {
        banners.append(h('div.banner.info', h('span', n, ' Lines you had edited are under ', h('b', 'Changed in mod'), '.'), h('div.spacer'),
          h('button.btn.sm', { title: 'Hide this message', onclick: async () => { await rpc('mod.dismissNotice', { id, plugin: plugin.fileName, text: n }); reload(); } }, 'Dismiss')));
        continue;
      }
      banners.append(h('div.banner.bad', h('span', n), h('div.spacer'), h('button.btn.sm', { title: 'Add the .strings files of a localized plugin', onclick: uploadStrings }, 'Add strings files…')));
    }
    if (mod.uploadedMasters.length) {
      banners.append(h('div.banner.info', h('span', `Uploaded masters kept in this mod: ${mod.uploadedMasters.join(', ')}`), h('div.spacer'),
        h('button.btn.sm', { title: 'Delete the master plugins you added for reading this mod (your own copies elsewhere are untouched)', onclick: async () => { if (await confirmBox('Delete uploaded masters?', 'They are only needed for reading the plugin. You can upload them again for the next update.', 'Delete', true)) { await rpc('mod.deleteMasters', { id }); reload(); } } }, 'Delete to save space')));
    }
  }

  function renderTabs() {
    clear(tabs);
    if (!mod.plugins.length) { tabs.classList.add('hidden'); return; }
    tabs.classList.remove('hidden');
    const label = (p) => `${p.fileName}  ·  ${p.isTopicFile ? 'topic file' : p.needsFix ? `fix ${p.fixes}` : 'no fix'}${p.unreviewed ? `  ·  ${p.unreviewed} to review` : ''}`;
    const sel = h('select.mono', { title: 'The plugin (or topic file) you are working on', style: { maxWidth: '520px' } },
      mod.plugins.map((p) => h('option', { value: p.fileName, selected: p === plugin }, label(p))));
    sel.addEventListener('change', () => { plugin = mod.plugins.find((p) => p.fileName === sel.value); save(); renderAll(); });
    const unrev = mod.plugins.filter((p) => p.unreviewed).length;
    const drop = h('span.chip', { title: 'Drop plugins or topic files here (or click to choose). A newer version of a plugin already in this mod updates it and keeps your edits.' }, '+ drop file / update');
    wireDrop(drop, importPaths);
    drop.addEventListener('click', () => addFiles());
    tabs.append(h('span.faint', 'Plugin'), sel,
      mod.plugins.length > 1 ? h('span.badge.b-fixed', { title: mod.plugins.map((p) => p.fileName).join('\n') }, `${mod.plugins.length} plugins in this mod`) : null,
      unrev && mod.plugins.length > 1 ? h('span.badge.b-broken', { title: 'Plugins with flagged lines not reviewed yet' }, `${unrev} with lines to review`) : null,
      drop, h('div.spacer'));
    if (plugin && !plugin.isTopicFile) {
      if (plugin.importedFixPlugin) tabs.append(h('button.appr.on.demo.fix.fix-remove', { title: `DBVO Fix plugin imported: ${plugin.importedFixPlugin}\nClick to remove it: the lines it changed get their earlier text back`, onclick: removeFix }, 'Fix'));
      tabs.append(h('button.btn.sm', { onclick: importFix, title: 'Take the texts of a DBVO Fix plugin you made earlier (for example with xEdit) into this plugin\'s lines' },
        plugin.importedFixPlugin ? `Imported: ${plugin.importedFixPlugin} · import again…` : 'Import existing DBVO Fix plugin…'));
    }
    if (plugin && plugin.playerNames) {
      tabs.append(h('button.btn.sm', { onclick: () => exportPlayerNames([]), title: 'Save the <Alias=Player> lines as a topic .txt in Downloads, for your player-name patches' }, `Export ${plugin.playerNames} player-name line(s)`));
    }
    if (plugin) tabs.append(h('button.btn.sm.ghost.danger', { onclick: deletePlugin, title: 'Remove this plugin (or topic file) and its Phase 1 work from the mod' }, 'Remove plugin…'));
  }

  async function deletePlugin() {
    const edited = plugin.lines.filter((l) => l.userEdited).length;
    const fixes = plugin.fixes || 0;
    const ok = await confirmBox(`Remove ${plugin.fileName}?`,
      `This removes the plugin from "${mod.name}" with all its Phase 1 work:\n• ${plugin.lines.length} lines\n• ${edited} line(s) you edited\n• ${fixes} line(s) in its DBVO Fix plugin\n• your approvals, variants and reviews\n\nDBVO Patches using this mod lose these lines the next time they open (takes already generated stay in the patch). A backup is made first, and you can add the plugin again later, but your edits only come back from the backup.`,
      'Remove', true);
    if (!ok) return;
    try {
      mod = await rpc('mod.deletePlugin', { id, plugin: plugin.fileName });
      plugin = mod.plugins[0] || null;
      save();
      renderAll();
      toast('Plugin removed. A backup was made first.');
    } catch (e) { fail(e); }
  }

  function renderFilters() {
    const c = counts();
    const chip = (key, label, title) => h('span.chip' + (filter === key ? '.on' : ''), { title: title || '', onclick: () => { filter = key; save(); renderFilters(); grid.setRows(visibleLines()); } }, label, h('span.n', c[key]));
    clear(filters);
    if (!plugin) return;
    const q = h('input', { type: 'text', placeholder: 'Search text, FormID, EditorID…', value: search, title: 'Search the text, FormID or EditorID' });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visibleLines()); }, 150));
    // Only filters that match at least one line (the open one always stays).
    const opt = (key, label, title) => (key === 'all' || filter === key || c[key] ? chip(key, label, title) : null);
    const legend = h('div.legend',
      h('span.legend-item', h('span.appr.on.demo.fix', 'Fix'), '= Edit in the DBVO Fix plugin'),
      h('span.legend-item', h('span.appr.on.demo', h('span.note', '♪')), '= Voiced in Phase 2'),
      h('span.legend-item', h('span.demo-ex', icon('ban', { size: 14 })), '= Not voiced (excluded)'));
    filters.append(h('div.filter-chips', opt('all', 'All', 'Every player line of this plugin'), opt('fixes', 'In DBVO Fix', 'Lines whose in-game text goes into the DBVO Fix plugin'), opt('notvoiced', 'Not voiced', 'Lines that won\'t be voiced in Phase 2'), opt('flagged', 'Flagged', 'BROKEN or TOO LONG WILL CTD lines to review'), opt('unreviewed', 'Not reviewed', 'Lines you haven\'t looked at yet'),
      opt('stage', 'Stage directions', 'Lines that are only an action, such as (Intimidate)'), opt('player', 'Player names', 'Lines with the player\'s name, voiced in a player-name patch'),
      opt('changed', 'Changed in mod', 'Lines a newer version of the mod changed'), opt('edited', 'Edited by you', 'Lines whose in-game text you edited'), opt('alias', 'Alias lines', 'Lines with an <Alias=…> that shows as a name or place in game: add iterations for each way it can read'), opt('iter', 'With iterations', 'Lines that add written-out iterations to the topic file'), opt('hidden', 'Hidden', 'Lines that look like an EditorID rather than something the player says: hidden and unvoiced until you click ♪'), opt('excluded', 'Excluded', 'Lines left out of voicing'), opt('removed', 'Removed', 'Lines a newer version of the mod removed'), h('div.spacer'), legend),
      h('div.filter-tools',
        h('span.legend-note', 'Review ', h('b', 'BROKEN'), ' and ', h('b', 'TOO LONG WILL CTD'), ' lines before Phase 2.'),
        h('div.spacer'), helpButton('review-the-lines'),
        h('button.btn.sm.ghost', { title: 'Find text in the in-game text of every line shown and replace it (goes into the DBVO Fix plugin like any edit)', onclick: findReplace }, 'Find and replace in all shown…'),
        searchLamp(q), columnsMenu(grid),
        h('button.btn.sm', { title: 'Show or hide the editing panel', onclick: () => { showInsp = !showInsp; store.set('insp1', showInsp); split.classList.toggle('no-insp', !showInsp); renderFilters(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel')));
  }
  function findReplace() {
    findReplaceDialog({
      pool: grid.rows, idOf: (l) => l.key, textOf: (l) => l.final, what: 'in-game text',
      apply: async (items) => { try { mod = await rpc('line1.replaceTexts', { modId: mod.id, plugin: plugin.fileName, items }); plugin = mod.plugins.find((p) => p.fileName === plugin.fileName); renderAll(); } catch (e) { fail(e); } } });
  }

  function renderActions() {
    clear(actions);
    if (!plugin) return;
    const sel = [...grid.selected];
    const c = counts();
    actions.append(
      h('span.stat', h('b', plugin.topicLines), ' topic lines · ',
        plugin.isTopicFile ? h('span.faint', 'topic file (no DBVO Fix plugin)')
          : plugin.needsFix ? h('span', { style: { color: 'var(--link)' } }, `${plugin.fixFileName} (${c.fixes} lines)`)
            : h('span', { style: { color: 'var(--ok)' } }, 'no DBVO Fix plugin needed'),
        c.unreviewed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.unreviewed} flagged not reviewed`) : null),
      helpButton('fix-voice-and-exclude'),
      (() => {
        const pool = (sel.length ? sel.map((k) => plugin.lines.find((x) => x.key === k)).filter(Boolean) : visibleLines()).filter((l) => l.hasOtherAlias && !l.isPlayerName && l.status !== 'Removed');
        return pool.length ? h('button.btn.sm', { title: `Write the alias lines ${sel.length ? 'you ticked' : 'shown'} out as every name or place they can show in game (one topic line each). The lines themselves are not changed.`, onclick: () => iterationsDialog(pool) }, `Iterations for ${pool.length} alias line(s)…`) : null;
      })(),
      h('div.spacer'),
      sel.length ? h('span.dim', `${sel.length} selected`) : null,
      sel.length ? h('button.btn.sm', { title: 'Use the script\'s suggestion for the ticked lines', onclick: () => bulk('accept') }, 'Use suggestions') : null,
      sel.length ? h('button.btn.sm', { title: 'Undo edits on the ticked lines', onclick: () => bulk('revert') }, 'Back to original') : null,
      sel.length ? h('button.btn.sm', { title: 'Confirm voicing and the DBVO Fix edit as they stand, and mark the rows reviewed (stage directions stay silent)', onclick: () => bulk('approve') }, 'Approve') : null,
      sel.length ? h('button.btn.sm', { title: 'Voice the ticked lines (topic file)', onclick: () => bulk('voiceOn') }, '♪ Voice') : null,
      sel.length ? h('button.btn.sm', { title: 'Don\'t voice the ticked lines', onclick: () => bulk('voiceOff') }, '♪ Silent') : null,
      sel.length ? h('button.btn.sm', { title: 'Write the ticked lines\' edits to the DBVO Fix plugin', onclick: () => bulk('fixOn') }, 'Fix on') : null,
      sel.length ? h('button.btn.sm', { title: 'Keep the ticked lines\' edits out of the DBVO Fix plugin', onclick: () => bulk('fixOff') }, 'Fix off') : null,
      sel.length ? h('button.btn.sm', { title: 'Mark the ticked rows as looked at, without changing anything', onclick: () => bulk('review') }, 'Mark reviewed') : null,
      sel.length ? h('button.btn.sm', { title: 'Leave the ticked lines out of the topic file and DBVO Fix plugin', onclick: () => bulk('exclude') }, 'Exclude') : null,
      sel.length ? h('button.btn.sm', { title: 'Bring excluded lines back', onclick: () => bulk('include') }, 'Include') : null,
      h('button.btn.sm.ghost', { title: 'Tick or untick every row in the current filter', onclick: () => grid.selectAll(!sel.length) }, sel.length ? 'Clear selection' : 'Select all shown'),
      h('button.btn.primary', { title: 'Write the topic files and DBVO Fix plugins, then open the DBVO Patches', onclick: toPhase2 }, 'Continue to Phase 2 →'));
  }

  // ---- inspector
  function approvalPanel(l) {
    if (l.status === 'Removed' || l.excluded) return null;
    const tag = (explicit) => h('span.faint', explicit ? ' (your choice)' : ' (default)');
    return h('div.box', { style: { display: 'flex', flexDirection: 'column', gap: '8px' } },
      h('div.field-label', { style: { margin: 0 } }, 'What happens with this line'),
      l.stage ? h('div.help', { style: { margin: 0 } }, h('b', 'Stage direction. '),
        'The whole line is in (parentheses) or [brackets]. Some are meant to be heard, like (Laughs) with the Eleven v4 model; others, like (Sip your drink), are silent subtitles. Your call:',
        h('div.row', { style: { marginTop: '6px' } },
          h('button.btn.sm' + (l.voiceApproved === true ? '.primary' : ''), { title: 'Voice it: Phase 2 starts its spoken text as an audio tag, e.g. [laughs]', onclick: () => act(l, 'voice', true) }, '♪ Voice it'),
          h('button.btn.sm' + (l.voiceApproved === false ? '.primary' : ''), { title: 'Keep it silent: the subtitle changes, nothing is generated', onclick: () => act(l, 'voice', false) }, 'Keep silent'))) : null,
      switchEl(l.voiced, (v) => act(l, 'voice', v), h('span', '♪ Voice it — in the topic file, generated in Phase 2', tag(l.voiceApproved != null)), { title: 'Off: no sound file is made for this line' }),
      plugin.isTopicFile ? null : switchEl(l.inFix, (v) => act(l, 'fix', v), h('span', 'Fix the subtitle — write this text to ' + plugin.fixFileName, tag(l.fixApproved != null)),
        { disabled: !l.changed, title: l.changed ? 'Off: the game keeps the plugin\'s original text' : 'The text is unchanged, so there is nothing to write' }),
      l.voiceApproved != null || l.fixApproved != null ? h('button.btn.sm.ghost', { style: { alignSelf: 'flex-start' }, title: 'Forget your choices and use the defaults again', onclick: async () => { await act(l, 'voice', null); await act(l, 'fix', null); } }, 'Back to defaults') : null,
      l.voiced && l.final && l.final.includes('*') ? h('div.check.bad', 'Still contains *: ElevenLabs reads it as "asterisk".') : null,
      l.flagged && !l.stage ? h('button.btn.sm.primary', { style: { alignSelf: 'flex-start' }, title: 'Confirm both choices as shown and mark the line reviewed', onclick: () => act(l, 'approve') }, 'Approve') : null);
  }

  function helpFor(l) {
    if (l.isPlayerName) {
      return h('div.help.warn',
        h('p', h('b', 'Player name line. '), 'The subtitle shows the player\'s chosen name where <Alias=Player> is, so it can\'t be voiced in this patch. DBVO modders voice these in separate player-name patches: one topic file per name, with the alias replaced by that name.'),
        h('p', 'Export the line below. It is saved as a topic .txt in your ', h('b', 'Downloads'), ' folder; add it to your master player-name topic file. More player-name lines to patch are collected on the ', h('b', 'DBVO Repository'), ' mod page on Nexus Mods.'),
        l.needsOverride ? h('p', 'The script also reformatted this line, so the formatted version goes into the DBVO Fix plugin.') : null);
    }
    if (l.stage && (l.kind === 'Broken' || l.kind === 'TooLong')) return null; // the approval panel explains stage directions
    if (l.kind === 'Broken') {
      return h('div.help.bad',
        h('p', h('b', 'BROKEN LINE. '), 'The script can\'t make this line voice-ready on its own: usually an alias placeholder like <Alias=…>, asterisks such as *laughs*, or a < or parenthesis in the wrong place.'),
        h('ul',
          h('li', 'Reword it in the box below so the subtitle reads well. Stage directions can go in a trailing (parenthesis): DBVO keeps that out of the voice and the filename. For example "No, thank you. *laughs*" can become "No, thank you. (laughs)".'),
          l.hasOtherAlias ? h('li', 'For an alias, either reword the line so no placeholder remains, or keep it and add iterations (+ Iter. in the list, or Iterations below): the line written out with each name or place it can show in game, one voice file each.') : null,
          h('li', 'Your text goes into the DBVO Fix plugin and the topic file automatically. No need to copy it anywhere by hand.'),
          h('li', 'Left as is, the plugin keeps its original line and this line stays unvoiced.')));
    }
    if (l.kind === 'TooLong') {
      return h('div.help.warn',
        h('p', h('b', 'TOO LONG WILL CTD. '), `Lines over 150 characters crash DBVO. Shorten it below 150 characters while keeping the meaning.`),
        h('ul',
          h('li', 'The shortened text replaces the subtitle in game (through the DBVO Fix plugin) and is what gets voiced.'),
          h('li', 'Left as is, the plugin keeps its original line, and this line is not voiced.')));
    }
    return null;
  }

  function renderInspector() {
    clear(insp);
    const l = focusedKey && plugin && plugin.lines.find((x) => x.key === focusedKey);
    if (!l) {
      insp.append(h('h3', 'Line'), h('div.dim', 'Click a row to see and edit it in full.'),
        h('div.help', h('p', h('b', 'Fixed'), ' rows were reformatted by the script and go into the DBVO Fix plugin.'),
          h('p', h('b', 'BROKEN LINE'), ' and ', h('b', 'TOO LONG WILL CTD'), ' rows need your decision; click one for guidance.'),
          h('p', h('b', 'PLAYER NAME'), ' rows contain <Alias=Player> and are voiced in player-name patches instead.')));
      return;
    }
    const finalBox = h('textarea', { rows: 3 }, l.final ?? '');
    const counter = h('span.counter');
    const checkEl = h('div');
    const check = debounce(async () => {
      const text = finalBox.value;
      counter.textContent = `${text.trim().length} / 150`;
      counter.classList.toggle('over', text.trim().length > 150);
      clear(checkEl);
      if (!text.trim()) return;
      try {
        const r = await rpc('rules.check', { text });
        if (r.reasons && r.reasons.length) {
          checkEl.append(h('div.check.bad', h('b', 'Not accepted by the script yet:'), h('ul', { style: { margin: '4px 0 0', paddingLeft: '18px' } }, r.reasons.map((x) => h('li', x))),
            r.fix && r.fix !== text.trim() ? h('div', { style: { marginTop: '6px' } }, h('button.btn.sm', { title: 'Replace the text with what the script would make of it', onclick: () => { finalBox.value = r.fix; act(l, 'setFinal', r.fix); } }, 'Use the script\'s version: ' + r.fix)) : null));
        } else if (r.stage) checkEl.append(h('div.check.ok', '✓ A stage direction: choose below whether it is voiced or stays silent.'));
        else if (r.problem) checkEl.append(h('div.check.bad', r.problem));
        else if (text !== l.original) checkEl.append(h('div.check.ok', r.isPlayerName ? '✓ Valid player-name line' : '✓ The script accepts this line'));
      } catch { /* advisory */ }
    }, 250);
    detofu(finalBox);
    finalBox.addEventListener('input', check);
    finalBox.addEventListener('change', () => act(l, 'setFinal', finalBox.value));
    check();

    insp.append(
      h('div.row.wrap', kindBadge(l), l.formId ? h('span.mono.dim', l.formId) : null, h('span.dim', l.record)),
      l.editorId ? h('div.mono.faint.ellipsis', { title: l.editorId }, l.editorId) : null,
      helpFor(l),
      h('div', h('div.field-label', 'Original'), h('div.box', l.original)),
      l.suggested && l.suggested !== l.original ? h('div', h('div.field-label', 'Script suggestion'), h('div.box.suggest', l.suggested)) : null,
      h('div', h('div.row', h('div.field-label.grow', plugin.isTopicFile ? 'Final line (topic file)' : 'Final in-game line (topic file + DBVO Fix plugin)'), counter), finalBox, checkEl,
        h('div.row', { style: { marginTop: '6px' } },
          l.suggested && l.suggested !== l.final ? h('button.btn.sm', { title: 'Replace the text with the script\'s suggestion', onclick: () => act(l, 'accept') }, 'Use suggestion') : null,
          l.final != null && l.final !== l.original ? h('button.btn.sm', { title: 'Undo your edit and use the in-game text', onclick: () => act(l, 'revert') }, 'Back to original') : null,
          h('div.spacer'),
          h('button.btn.sm', { title: l.excluded ? 'Bring this line back' : 'Leave this line out of the topic file and DBVO Fix plugin', onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? 'Include' : 'Exclude'))),
      approvalPanel(l),
      l.isPlayerName ? h('div.row', h('button.btn.primary.sm', { title: 'Write this player-name line to your Downloads folder, to voice it in a player-name patch', onclick: () => exportPlayerNames([l.key]) }, 'Export this line to Downloads'),
        l.playerNameExported ? h('span.badge.b-valid', 'exported') : null) : null,
      l.status !== 'Removed' && !l.isPlayerName ? variantsEditor(l) : null,
      l.hiddenByPrompts ? h('label.row', h('input', { type: 'checkbox', checked: l.includeAnyway, onchange: (e) => act(l, 'includeHidden', e.target.checked) }),
        'Voice it anyway (it looks like an EditorID rather than a line the player says, so it started out hidden)') : null,
      l.status === 'ChangedInMod' ? h('div.help.warn', h('p', h('b', 'The mod changed this line.')),
        h('p', h('span.dim', 'Was: '), l.previousOriginal || '—'), h('p', h('span.dim', 'Your earlier final: '), l.previousFinal || '—'),
        h('div.row', h('button.btn.sm', { title: 'The mod update changed this line; keep your edit', onclick: () => act(l, 'keepPrevious') }, 'Keep my earlier edit'), h('button.btn.sm', { title: 'Drop your edit and use the updated text', onclick: () => act(l, 'acceptChange') }, 'Use the new text'))) : null,
    );
    if (!l.reviewed && l.flagged) act(l, 'review');
  }

  function variantsEditor(l) {
    const n = (l.variants || []).length;
    return h('div', h('div.row', h('div.field-label.grow', `Iterations (${n})`),
        h('button.btn.sm' + (l.hasOtherAlias ? '.primary' : ''), { title: 'Write the line out every way it shows in game: one topic line (and voice file) each. The line itself is not changed.', onclick: () => iterationsDialog([l]) }, n ? 'Edit iterations…' : '+ Add iterations…')),
      n ? h('div.box', { style: { maxHeight: '160px', overflow: 'auto', fontSize: '12.5px' } }, l.variants.map((v) => h('div', v))) : h('div.faint', { style: { fontSize: '12.5px' } }, l.hasOtherAlias ? 'This line has an <Alias=…>: add an iteration for each name or place it can show as in game. The line stays as it is.' : 'Extra versions of this line that also go into the topic file.'));
  }

  async function bulk(action) {
    try {
      mod = await rpc('line1.bulk', { modId: mod.id, plugin: plugin.fileName, keys: [...grid.selected], action });
      plugin = mod.plugins.find((p) => p.fileName === plugin.fileName);
      grid.selectAll(false);
      renderAll();
    } catch (e) { fail(e); }
  }

  async function importPaths(paths) {
    if (!paths || !paths.length) return;
    try {
      const real = mod.plugins.filter((p) => !p.isTopicFile);
      if (real.length) {
        const info = await rpc('plugin.inspect', { paths });
        const fixes = info.filter((x) => x.looksLikeFix);
        for (const fx of fixes) {
          const name = fx.path.split('\\').pop();
          const guess = real.find((p) => fx.masters.some((m) => m.toLowerCase() === p.fileName.toLowerCase())) || plugin || real[0];
          const target = h('select', real.map((p) => h('option', { value: p.fileName, selected: p === guess }, p.fileName)));
          const choice = await modal('This looks like a DBVO Fix plugin', [
            h('div', h('span.mono', name), ' only overrides dialogue of another plugin, so it is probably an earlier DBVO Fix plugin, not a mod plugin to voice.'),
            h('div.col', h('div.field-label', 'Import its texts into'), target),
          ], [{ label: 'Skip this file', value: 'skip' }, { label: 'Add as a plugin anyway', value: 'plugin' }, { label: 'Import as DBVO Fix', primary: true, value: 'fix' }]);
          if (!choice || choice === 'skip') { paths = paths.filter((x) => x !== fx.path); continue; }
          if (choice === 'fix') {
            paths = paths.filter((x) => x !== fx.path);
            const r = await rpc('mod.importFix', { id, plugin: target.value, path: fx.path });
            mod = r.mod;
            plugin = mod.plugins.find((p) => p.fileName === target.value) || plugin;
            toast(`${r.applied} line(s) taken from ${name}.`);
          }
        }
        if (!paths.length) { renderAll(); return; }
      }
      toast('Reading files…');
      const r = await rpc('mod.import', { id: mod.id, paths });
      mod = r.mod;
      const last = r.reports[r.reports.length - 1];
      plugin = mod.plugins.find((p) => p.fileName === last.plugin) || mod.plugins[0];
      renderAll();
      toast(r.reports.map((x) => x.changed || x.removed || x.kept
        ? `${x.plugin}: update — ${x.kept} kept, ${x.changed} changed, ${x.added} new, ${x.removed} removed`
        : `${x.plugin}: ${x.added} lines read`).join(' · '));
    } catch (e) { fail(e); }
  }

  async function addFiles(kind) {
    const filter = kind === 'plugins' ? 'Skyrim plugins|*.esp;*.esm;*.esl' : kind === 'topics' ? 'Topic files|*.txt' : 'Plugins and topic files|*.esp;*.esm;*.esl;*.txt';
    const paths = await rpc('pick.files', { title: kind === 'topics' ? 'Choose topic files' : 'Choose plugins', filter });
    if (paths) importPaths(paths);
  }

  const storedPaths = () => mod.plugins.map((p) => `${app.state.libraryRoot}\\mods\\${mod.id}\\${p.isTopicFile ? 'topics' : 'plugins'}\\${p.fileName}`);

  async function uploadMasters() {
    const paths = await rpc('pick.files', { title: 'Choose master plugins', filter: 'Skyrim plugins|*.esp;*.esm;*.esl' });
    if (!paths) return;
    await rpc('mod.addMasters', { id, paths });
    importPaths(storedPaths());
  }

  async function uploadStrings() {
    const paths = await rpc('pick.files', { title: 'Choose the plugin\'s strings files', filter: 'Strings|*.strings;*.dlstrings;*.ilstrings' });
    if (!paths) return;
    await rpc('mod.addStrings', { id, paths });
    importPaths(storedPaths());
  }

  async function importFix() {
    const path = await rpc('pick.file', { title: `Choose the earlier DBVO Fix plugin for ${plugin.fileName}`, filter: 'Skyrim plugins|*.esp;*.esm;*.esl' });
    if (!path) return;
    try {
      const r = await rpc('mod.importFix', { id, plugin: plugin.fileName, path });
      mod = r.mod;
      plugin = mod.plugins.find((p) => p.fileName === plugin.fileName);
      renderAll();
      await modal('DBVO Fix plugin imported', [h('div.kv',
        h('div', 'Lines taken from it'), h('div', r.applied),
        h('div', 'Old markers ignored'), h('div', `${r.markers} (BROKEN LINE / TOO LONG WILL CTD texts stay flagged for you to fix)`),
        h('div', 'Records not found in the plugin'), h('div', r.unmatched)),
        h('div.faint', 'From now on this app writes the DBVO Fix plugin, rebuilt from the plugin with your texts.')]);
    } catch (e) { fail(e); }
  }

  async function removeFix() {
    const name = plugin.importedFixPlugin;
    let path = '';
    if (plugin.fixUndo) {
      if (!await confirmBox('Remove the imported DBVO Fix plugin?', `The lines ${name} changed get their earlier text back. Lines you edited by hand after importing keep your edit. The file itself is not touched.`, 'Remove', true)) return;
    } else {
      // Imported by 7.3 or older: the earlier texts weren't kept, so the fix plugin itself shows which lines it set.
      const v = await modal('Remove the imported DBVO Fix plugin?', [
        h('div', `${name} was imported by an older version of DBVO Studio, which didn't keep the lines' earlier text.`),
        h('div', 'Choose the same DBVO Fix plugin file again: every line that still reads as that plugin wrote it goes back to the script\'s version. Lines you edited by hand since keep your edit.'),
        h('div.faint', 'Without the file, the import is removed but its texts stay; ↶ on a row then takes that line back to the original.'),
      ], [{ label: 'Cancel', value: null }, { label: 'Remove, keep the texts', value: 'keep' }, { label: 'Choose the file…', primary: true, value: 'file' }]);
      if (!v) return;
      if (v === 'file') {
        path = await rpc('pick.file', { title: `Choose ${name} again`, filter: 'Skyrim plugins|*.esp;*.esm;*.esl' });
        if (!path) return;
      }
    }
    try {
      const r = await rpc('mod.removeFix', { id, plugin: plugin.fileName, path });
      mod = r.mod;
      plugin = mod.plugins.find((p) => p.fileName === plugin.fileName);
      renderAll();
      toast(r.restored || r.kept
        ? `${name} removed: ${r.restored} line(s) back to their earlier text${r.kept ? `, ${r.kept} kept your later edit` : ''}.`
        : `${name} removed. ${path ? 'No line still read as it wrote them.' : 'Its texts stay: use ↶ on a row to go back to the original.'}`);
    } catch (e) { fail(e); }
  }

  async function exportPlayerNames(keys) {
    try {
      const r = await rpc('mod.exportPlayerNames', { id, plugin: plugin.fileName, keys });
      mod = r.mod;
      plugin = mod.plugins.find((p) => p.fileName === plugin.fileName);
      renderAll();
      await modal('Player-name lines exported', [
        h('div', `${r.count} line(s) saved to:`), pathList([r.file]),
        h('div.help', 'Add these lines to your master player-name topic file. More player-name lines to patch are collected on the DBVO Repository mod page on Nexus Mods.'),
      ], [{ label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }]).then((v) => { if (v === 'open') rpc('open.path', { path: r.file }); });
    } catch (e) { fail(e); }
  }

  async function editMeta() {
    const r = await prompt('Mod details', [
      { name: 'name', label: 'Mod name', value: mod.name },
      { name: 'version', label: 'Mod version', value: mod.version },
      { name: 'nexusUrl', label: 'Nexus page (optional)', value: mod.nexusUrl },
    ]);
    if (!r) return;
    mod = await rpc('mod.saveMeta', { id, ...r });
    renderHead();
  }

  async function delMod() {
    // Say exactly what goes and what stays, and which DBVO Patches lose their lines.
    const users = (app.state.patches || []).filter((p) => (p.modIds || []).includes(id));
    const ok = await modal('Delete mod?', [
      h('div', `Deletes "${mod.name}" and its Phase 1 work: the line review, your text edits, the DBVO Fix decisions and the copies of its plugins kept in the Library.`),
      h('div', h('b', 'Stays: '), 'your original plugin files, and every DBVO Fix plugin, topic file, WAV, LIP and .fuz already exported to your folders.'),
      users.length ? h('div.banner.warn', { style: { margin: 0 } }, h('div', h('b', `${users.length} DBVO Patch(es) read this mod: `), users.map((p) => p.name).join(', '), '. They lose its lines. Their takes are kept in the patch, and come back if you import the mod again and tick it in the patch\'s Folders & options → Mods.')) : null,
      h('div.faint', 'A backup of the project files is made first (backups hold no audio).'),
    ], [{ label: 'Cancel', value: false }, { label: 'Delete', danger: true, value: true }]);
    if (!ok) return;
    await rpc('mod.delete', { id });
    go('mods');
  }

  async function exportOutputs() {
    const folder = await rpc('pick.folder', { title: 'Folder for topic files and DBVO Fix plugins' });
    if (!folder) return;
    try {
      const ow = await askFixOverwrite(await rpc('mod.fixConflicts', { id, folder }).catch(() => []));
      if (ow == null) return;
      const written = await rpc('mod.writeOutputs', { id, fixFolders: [folder], topicFolder: folder, keepFix: !ow });
      await modal('Files written', [pathList(written)]);
    } catch (e) { fail(e); }
  }

  async function toPhase2() {
    const unreviewed = mod.plugins.flatMap((p) => p.lines.filter((l) => l.flagged && !l.reviewed && live(l)).map((l) => ({ p, l })));
    if (unreviewed.length) {
      const choice = await modal('Flagged lines not reviewed', [
        h('div', `${unreviewed.length} BROKEN LINE / TOO LONG WILL CTD row(s) were never opened. Unresolved rows get no override: the game keeps its original text and the line is not voiced.`),
        h('div.box', { style: { maxHeight: '200px', overflow: 'auto' } }, unreviewed.slice(0, 40).map(({ p, l }) => h('div.row', { style: { flexWrap: 'nowrap' } }, kindBadge(l), h('span.mono.faint', { style: { whiteSpace: 'nowrap', flexShrink: 0 } }, p.fileName), h('span.ellipsis', { style: { minWidth: 0 } }, l.original)))),
      ], [{ label: 'Continue anyway', value: 'go' }, { label: 'Show them', primary: true, value: 'show' }], { help: 'continue-to-phase-2' });
      if (!choice) return;
      if (choice === 'show') {
        plugin = unreviewed[0].p;
        filter = 'unreviewed';
        save();
        renderAll();
        return;
      }
    }
    try {
      const r = await rpc('patch.openFor', { modId: mod.id });
      if (r.needVoicePack) {
        toast('Create a voice pack first (Global Settings → Voice packs). Phase 2 opens right after.', true);
        go('settings', { tab: 'voices', id: 'new' });
        return;
      }
      go('patch', { id: r.id });
    } catch (e) { fail(e); }
  }

  function save() { store.set('mod:' + id, { plugin: plugin && plugin.fileName, filter }); }

  function renderAll() {
    renderHead();
    renderBanners();
    renderTabs();
    renderFilters();
    split.classList.toggle('hidden', !plugin);
    grid.setRows(visibleLines());
    renderInspector();
    renderActions();
  }

  renderAll();
  return view;
}

export function wireDrop(el, onPaths) {
  el.addEventListener('dragover', (e) => { e.preventDefault(); el.classList.add('over'); });
  el.addEventListener('dragleave', () => el.classList.remove('over'));
  el.addEventListener('drop', async (e) => {
    e.preventDefault();
    e.stopPropagation();
    el.classList.remove('over');
    const files = [...(e.dataTransfer.files || [])];
    if (!files.length) return;
    try { onPaths(await droppedPaths(files)); } catch (err) { fail(err); }
  });
}
