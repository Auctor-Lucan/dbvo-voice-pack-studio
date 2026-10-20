// Phase 1: Mods list and the per-plugin workspace.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, droppedPaths, debounce, popupMenu } from './lib.js';
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
      onchange(r.n.trim());
    } else onchange(sel.value);
  });
  return sel;
}

// ------------------------------------------------------------------ list

export async function modsView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'Mods'),
    h('span.sub', 'Phase 1 · read plugins or topic files and make their lines DBVO-ready. Shared by every voice pack.'),
    h('div.spacer'),
    h('button.btn.primary', { onclick: newMod }, '+ New mod')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.mods.length) {
    body.append(h('div.empty', h('h2', 'No mods yet'),
      h('div', 'Create a mod, then drop its .esp / .esm files (or topic .txt files) into it.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { onclick: newMod }, '+ New mod'))));
    return view;
  }
  const q = h('input', { type: 'text', placeholder: 'Filter mods…', style: { width: '260px', marginBottom: '12px' } });
  const listEl = h('div');
  const render = () => {
    const f = q.value.toLowerCase();
    clear(listEl);
    const cats = [...s.settings.categories, ...new Set(s.mods.map((m) => m.category).filter((c) => !s.settings.categories.includes(c)))];
    for (const cat of cats) {
      const mods = s.mods.filter((m) => (m.category || 'Misc') === cat && (!f || m.name.toLowerCase().includes(f)));
      if (!mods.length) continue;
      listEl.append(h('div.cat', h('div.cat-head', h('h3', cat), h('span.faint', mods.length)),
        mods.map((m) => h('div.prow', { style: { gridTemplateColumns: 'minmax(200px,2fr) minmax(200px,3fr) 220px' }, onclick: () => go('mod', { id: m.id }) },
          h('div.ellipsis', h('b', m.name), m.version ? h('span.faint', '  v' + m.version) : null),
          h('div.faint.ellipsis.mono', m.plugins.map((p) => p.fileName).join(', ') || 'no plugins yet'),
          h('div.row', { style: { justifyContent: 'flex-end' } },
            m.plugins.some((p) => p.unreviewed) ? h('span.badge.b-broken', `${m.plugins.reduce((a, p) => a + p.unreviewed, 0)} to review`) : null,
            m.plugins.some((p) => p.needsFix) ? h('span.badge.b-fixed', 'DBVO Fix') : null,
            h('span.faint', `${m.plugins.reduce((a, p) => a + p.topicLines, 0)} lines`))))));
    }
  };
  q.addEventListener('input', render);
  render();
  body.append(q, listEl);
  return view;
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

  const view = h('div.view');
  const head = h('div.view-head');
  const hint = h('div.hint', h('b', 'Review flagged lines'), ' (BROKEN LINE, TOO LONG WILL CTD, PLAYER NAME), then ', h('b', 'Continue to Phase 2'),
    '. Your edits are saved as you go; Phase 2 reads them directly and writes the DBVO Fix plugin and topic file for you. "Export…" is only for using the files outside this app.');
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
      { id: 'type', title: 'Type', width: 170, render: (l) => h('div.row', kindBadge(l),
        l.status === 'ChangedInMod' ? h('span.badge.b-changed', 'changed in mod') : null,
        l.hiddenByPrompts ? h('span.badge.b-hidden', { title: 'Every response has its own prompt, so the game never shows this topic text.' }, l.includeAnyway ? 'hidden · incl.' : 'hidden') : null) },
      { id: 'formid', title: plugin && plugin.isTopicFile ? '' : 'FormID', width: 90, cls: 'mono dim', render: (l) => l.formId },
      { id: 'record', title: 'Record', width: 96, cls: 'dim', render: (l) => l.record },
      { id: 'orig', title: 'Original', flex: 1, width: 260, render: (l) => h('span', { title: l.original }, l.original) },
      { id: 'final', title: 'Final in-game line', flex: 1.3, width: 320, render: (l) => finalInput(l) },
      { id: 'act', title: '', width: 92, resizable: false, render: (l) => h('div.row',
        l.suggested && l.final !== l.suggested ? h('button.icon-btn', { title: 'Use the script\'s suggestion', onclick: () => act(l, 'accept') }, '✓') : null,
        l.final != null && l.final !== l.original ? h('button.icon-btn', { title: 'Undo: back to the original text', onclick: () => act(l, 'revert') }, '↶') : null,
        h('button.icon-btn', { title: l.excluded ? 'Include again' : 'Exclude from the topic file', onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? '⊕' : '⊘')) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  function finalInput(l) {
    if (l.status === 'Removed') return h('span.faint', l.final || '');
    const inp = h('input', { type: 'text', value: l.final ?? '', placeholder: l.flagged ? 'Fix this line, or leave empty' : '' });
    if (l.needsOverride) inp.style.color = 'var(--link)';
    inp.addEventListener('change', () => act(l, 'setFinal', inp.value));
    inp.addEventListener('focus', () => { if (focusedKey !== l.key) { focusedKey = l.key; renderInspector(); } });
    return inp;
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
      flagged: L.filter((l) => l.flagged && live(l)).length,
      unreviewed: L.filter((l) => l.flagged && !l.reviewed && live(l)).length,
      player: L.filter((l) => l.isPlayerName && live(l)).length,
      changed: L.filter((l) => l.status === 'ChangedInMod').length,
      edited: L.filter((l) => l.userEdited).length,
      hidden: L.filter((l) => l.hiddenByPrompts).length,
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
        case 'flagged': if (!(l.flagged && live(l))) return false; break;
        case 'unreviewed': if (!(l.flagged && !l.reviewed && live(l))) return false; break;
        case 'player': if (!(l.isPlayerName && live(l))) return false; break;
        case 'changed': if (l.status !== 'ChangedInMod') return false; break;
        case 'hidden': if (!l.hiddenByPrompts) return false; break;
        case 'excluded': if (!l.excluded) return false; break;
        case 'removed': if (l.status !== 'Removed') return false; break;
        case 'edited': if (!l.userEdited) return false; break;
      }
      return !q || (l.original || '').toLowerCase().includes(q) || (l.final || '').toLowerCase().includes(q) || l.formId.toLowerCase().includes(q) || (l.editorId || '').toLowerCase().includes(q);
    });
  }

  function renderHead() {
    const exportBtn = h('button.btn', { disabled: !plugin }, 'Export… ▾');
    exportBtn.addEventListener('click', () => popupMenu(exportBtn, [
      { label: 'Topic files + DBVO Fix plugins to a folder…', sub: 'Only for using them outside this app. Phase 2 writes them for you.', onclick: exportOutputs },
    ]));
    clear(head).append(
      h('button.btn.ghost.sm', { onclick: () => go('mods') }, '← Mods'),
      h('h1', mod.name),
      mod.version ? h('span.sub', 'v' + mod.version) : null,
      h('button.icon-btn', { title: 'Rename / version', onclick: editMeta }, '✎'),
      h('span.faint', 'Category'),
      categorySelect(mod.category, async (c) => { try { await rpc('mod.setCategory', { id, category: c }); mod.category = c; await refreshState(); renderHead(); } catch (e) { fail(e); } }),
      h('div.spacer'),
      h('button.btn', { onclick: addFiles }, '+ Add plugins or topic files'),
      exportBtn,
      h('button.btn.ghost.danger', { onclick: delMod }, 'Delete'));
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
          m.nexusSearchUrl ? h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url: m.nexusSearchUrl }); } }, 'find on Nexus') : h('span.faint', '(set the base game folder in Settings)'))),
        h('div.spacer'),
        h('button.btn.sm', { onclick: uploadMasters }, 'Upload masters…')));
    }
    for (const n of plugin.notices) banners.append(h('div.banner.bad', h('span', n), h('div.spacer'), h('button.btn.sm', { onclick: uploadStrings }, 'Add strings files…')));
    if (mod.uploadedMasters.length) {
      banners.append(h('div.banner.info', h('span', `Uploaded masters kept in this mod: ${mod.uploadedMasters.join(', ')}`), h('div.spacer'),
        h('button.btn.sm', { onclick: async () => { if (await confirmBox('Delete uploaded masters?', 'They are only needed for reading the plugin. You can upload them again for the next update.', 'Delete', true)) { await rpc('mod.deleteMasters', { id }); reload(); } } }, 'Delete to save space')));
    }
  }

  function renderTabs() {
    clear(tabs);
    if (!mod.plugins.length) { tabs.classList.add('hidden'); return; }
    tabs.classList.remove('hidden');
    for (const p of mod.plugins) {
      tabs.append(h('span.chip' + (p === plugin ? '.on' : ''), { onclick: () => { plugin = p; save(); renderAll(); } },
        h('span.mono', p.fileName),
        p.isTopicFile ? h('span.n', '· topic file') : p.needsFix ? h('span.n', `· fix ${p.fixes}`) : h('span.n', '· clean'),
        p.unreviewed ? h('span.badge.b-broken', p.unreviewed) : null));
    }
    const drop = h('span.chip', { title: 'Drop a plugin or topic file. A newer version of an existing one updates it.' }, '+ drop file / update');
    wireDrop(drop, importPaths);
    drop.addEventListener('click', addFiles);
    tabs.append(drop, h('div.spacer'));
    if (plugin && !plugin.isTopicFile) {
      tabs.append(h('button.btn.sm', { onclick: importFix, title: 'Take the texts of a DBVO Fix plugin you made earlier (for example with xEdit) for this plugin' },
        plugin.importedFixPlugin ? `Imported: ${plugin.importedFixPlugin} · import again…` : 'Import existing DBVO Fix plugin…'));
    }
    if (plugin && plugin.playerNames) {
      tabs.append(h('button.btn.sm', { onclick: () => exportPlayerNames([]) }, `Export ${plugin.playerNames} player-name line(s)`));
    }
  }

  function renderFilters() {
    const c = counts();
    const chip = (key, label) => h('span.chip' + (filter === key ? '.on' : ''), { onclick: () => { filter = key; save(); renderFilters(); grid.setRows(visibleLines()); } }, label, h('span.n', c[key]));
    clear(filters);
    if (!plugin) return;
    const q = h('input', { type: 'text', placeholder: 'Search text, FormID, EditorID…', value: search });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visibleLines()); }, 150));
    filters.append(chip('all', 'All'), chip('fixes', 'In DBVO Fix'), chip('flagged', 'Flagged'), chip('unreviewed', 'Not reviewed'),
      c.player ? chip('player', 'Player names') : null,
      chip('changed', 'Changed in mod'), chip('edited', 'Edited by you'), chip('hidden', 'Hidden by prompts'), chip('excluded', 'Excluded'), chip('removed', 'Removed'),
      h('div.spacer'), q,
      h('button.btn.sm', { title: 'Show or hide the editing panel', onclick: () => { showInsp = !showInsp; store.set('insp1', showInsp); split.classList.toggle('no-insp', !showInsp); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
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
      h('div.spacer'),
      sel.length ? h('span.dim', `${sel.length} selected`) : null,
      sel.length ? h('button.btn.sm', { onclick: () => bulk('accept') }, 'Use suggestions') : null,
      sel.length ? h('button.btn.sm', { onclick: () => bulk('revert') }, 'Back to original') : null,
      sel.length ? h('button.btn.sm', { onclick: () => bulk('review') }, 'Mark reviewed') : null,
      sel.length ? h('button.btn.sm', { onclick: () => bulk('exclude') }, 'Exclude') : null,
      sel.length ? h('button.btn.sm', { onclick: () => bulk('include') }, 'Include') : null,
      h('button.btn.sm.ghost', { onclick: () => grid.selectAll(!sel.length) }, sel.length ? 'Clear selection' : 'Select all shown'),
      h('button.btn.primary', { onclick: toPhase2 }, 'Continue to Phase 2 →'));
  }

  // ---- inspector
  function helpFor(l) {
    if (l.isPlayerName) {
      return h('div.help', { style: { borderColor: '#4c2f7a', background: '#1f1533', color: '#dccbf5' } },
        h('p', h('b', 'Player name line. '), 'The subtitle shows the player\'s chosen name where <Alias=Player> is, so it can\'t be voiced in this patch. DBVO modders voice these in separate player-name patches: one topic file per name, with the alias replaced by that name.'),
        h('p', 'Export the line below. It is saved as a topic .txt in your ', h('b', 'Downloads'), ' folder; add it to your master player-name topic file. More player-name lines to patch are collected on the ', h('b', 'DBVO Repository'), ' mod page on Nexus Mods.'),
        l.needsOverride ? h('p', 'The script also reformatted this line, so the formatted version goes into the DBVO Fix plugin.') : null);
    }
    if (l.kind === 'Broken') {
      return h('div.help.bad',
        h('p', h('b', 'BROKEN LINE. '), 'The script can\'t make this line voice-ready on its own: usually an alias placeholder like <Alias=…>, asterisks such as *laughs*, or a < or parenthesis in the wrong place.'),
        h('ul',
          h('li', 'Reword it in the box below so the subtitle reads cleanly. Stage directions can go in a trailing (parenthesis): DBVO keeps that out of the voice and the filename. For example "No Serana. *laughs*" can become "No Serana. (laughs)".'),
          l.hasOtherAlias ? h('li', 'For an alias, either reword it so no placeholder remains, or add written-out versions as variants below (one file each).') : null,
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
        if (r.problem) checkEl.append(h('div.check.bad', r.problem));
        else if (text !== l.original) checkEl.append(h('div.check.ok', r.isPlayerName ? '✓ Valid player-name line' : '✓ The script accepts this line'));
      } catch { /* advisory */ }
    }, 250);
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
          l.suggested && l.suggested !== l.final ? h('button.btn.sm', { onclick: () => act(l, 'accept') }, 'Use suggestion') : null,
          l.final != null && l.final !== l.original ? h('button.btn.sm', { onclick: () => act(l, 'revert') }, 'Back to original') : null,
          h('div.spacer'),
          h('button.btn.sm', { onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? 'Include' : 'Exclude'))),
      l.needsOverride ? h('div.banner.info', { style: { margin: 0 } }, 'This text is written to ' + plugin.fixFileName + '.') : null,
      l.isPlayerName ? h('div.row', h('button.btn.primary.sm', { onclick: () => exportPlayerNames([l.key]) }, 'Export this line to Downloads'),
        l.playerNameExported ? h('span.badge.b-valid', 'exported') : null) : null,
      l.hasOtherAlias && !l.isPlayerName ? variantsEditor(l) : null,
      l.hiddenByPrompts ? h('label.row', h('input', { type: 'checkbox', checked: l.includeAnyway, onchange: (e) => act(l, 'includeHidden', e.target.checked) }),
        'Include anyway (every response in this plugin has its own prompt, so the game normally never shows this text)') : null,
      l.status === 'ChangedInMod' ? h('div.help.warn', h('p', h('b', 'The mod changed this line.')),
        h('p', h('span.dim', 'Was: '), l.previousOriginal || '—'), h('p', h('span.dim', 'Your earlier final: '), l.previousFinal || '—'),
        h('div.row', h('button.btn.sm', { onclick: () => act(l, 'keepPrevious') }, 'Keep my earlier edit'), h('button.btn.sm', { onclick: () => act(l, 'acceptChange') }, 'Use the new text'))) : null,
    );
    if (!l.reviewed && l.flagged) act(l, 'review');
  }

  function variantsEditor(l) {
    const ta = h('textarea', { rows: 4, placeholder: 'One line per variant, e.g.\nAlvor sent me. Riverwood is in danger.\nGerdur sent me. Riverwood is in danger.' }, (l.variants || []).join('\n'));
    return h('div', h('div.field-label', 'Written-out variants (each becomes its own topic line and file)'), ta,
      h('div.row', { style: { marginTop: '6px' } }, h('span.faint.grow', 'Write each exactly as the subtitle will read in game. The plugin is not changed.'),
        h('button.btn.sm', { onclick: () => act(l, 'variants', ta.value.split('\n')) }, 'Save variants')));
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

  async function addFiles() {
    const paths = await rpc('pick.files', { title: 'Choose plugins or topic files', filter: 'Plugins and topic files|*.esp;*.esm;*.esl;*.txt|Skyrim plugins|*.esp;*.esm;*.esl|Topic files|*.txt' });
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

  async function exportPlayerNames(keys) {
    try {
      const r = await rpc('mod.exportPlayerNames', { id, plugin: plugin.fileName, keys });
      mod = r.mod;
      plugin = mod.plugins.find((p) => p.fileName === plugin.fileName);
      renderAll();
      await modal('Player-name lines exported', [
        h('div', `${r.count} line(s) saved to:`), h('div.box.mono', r.file),
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
    if (!(await confirmBox('Delete mod?', `Delete "${mod.name}" and its Phase 1 work? A backup is made first.`, 'Delete', true))) return;
    await rpc('mod.delete', { id });
    go('mods');
  }

  async function exportOutputs() {
    const folder = await rpc('pick.folder', { title: 'Folder for topic files and DBVO Fix plugins' });
    if (!folder) return;
    try {
      const written = await rpc('mod.writeOutputs', { id, fixFolders: [folder], topicFolder: folder });
      await modal('Files written', [h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, written.join('\n'))]);
    } catch (e) { fail(e); }
  }

  async function toPhase2() {
    const unreviewed = mod.plugins.flatMap((p) => p.lines.filter((l) => l.flagged && !l.reviewed && live(l)).map((l) => ({ p, l })));
    if (unreviewed.length) {
      const choice = await modal('Flagged lines not reviewed', [
        h('div', `${unreviewed.length} BROKEN LINE / TOO LONG WILL CTD row(s) were never opened. Unresolved rows get no override: the game keeps its original text and the line is not voiced.`),
        h('div.box', { style: { maxHeight: '200px', overflow: 'auto' } }, unreviewed.slice(0, 40).map(({ p, l }) => h('div.row', kindBadge(l), h('span.mono.faint', p.fileName), h('span.ellipsis', l.original)))),
      ], [{ label: 'Continue anyway', value: 'go' }, { label: 'Show them', primary: true, value: 'show' }]);
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
        toast('Create a voice pack first (Settings → Voice packs). Phase 2 opens right after.', true);
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
