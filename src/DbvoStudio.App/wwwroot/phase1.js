// Phase 1: Mods list and the per-plugin workspace.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, droppedPaths, debounce, popupMenu, columnsMenu, switchEl } from './lib.js';
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
    h('span.sub', 'Read plugins or topic files and make their lines DBVO-ready. Shared by every voice pack.'),
    h('div.spacer'),
    h('button.btn.primary', { title: 'Create a mod project and add its plugins', onclick: newMod }, '+ New mod')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.mods.length) {
    body.append(h('div.empty', h('h2', 'No mods yet'),
      h('div', 'Create a mod, then drop its .esp / .esm files (or topic .txt files) into it.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Create a mod project and add its plugins', onclick: newMod }, '+ New mod'))));
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
        h('div.cards2', mods.map((m) => {
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
        }))));
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
  const hint = h('div.hint', h('b', 'Review flagged lines'), ' (BROKEN LINE, TOO LONG WILL CTD, PLAYER NAME), then ', h('b', 'Continue to Phase 2'), '. ',
    'A DBVO Fix plugin is only made when at least one line needs fixing; a clean plugin gets none. ',
    'Your edits are saved as you go. In Phase 2 they are written for you to the patch\'s DBVO Fix folder and its final folder, or use ', h('b', 'Export…'), ' (top right) to write them anywhere now.');
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
      { id: 'orig', title: 'Original', flex: 1, width: 260, render: (l) => h('span.dim', { title: l.original }, l.original) },
      { id: 'final', title: 'Final in-game line', flex: 1.3, width: 320, render: (l) => finalInput(l) },
      { id: 'appr', title: 'Voice / Fix', width: 104, render: (l) => approvalChips(l) },
      { id: 'act', title: '', width: 92, resizable: false, render: (l) => h('div.row',
        l.suggested && l.final !== l.suggested ? h('button.icon-btn', { title: 'Use the script\'s suggestion', onclick: () => act(l, 'accept') }, '✓') : null,
        l.final != null && l.final !== l.original ? h('button.icon-btn', { title: 'Undo: back to the original text', onclick: () => act(l, 'revert') }, '↶') : null,
        h('button.icon-btn', { title: l.excluded ? 'Include again' : 'Exclude from the topic file', onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? '⊕' : '⊘')) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  // Two independent approvals: voiced (topic file) and the edit in the DBVO Fix plugin.
  function approvalChips(l) {
    if (l.status === 'Removed' || l.excluded) return h('span.faint', l.excluded ? 'excluded' : '');
    const chip = (on, explicit, against, label, title, onclick) => h('button.appr' + (on ? '.on' : '') + (against ? '.against' : '') + (explicit ? '.explicit' : ''), { title, onclick }, label);
    const v = chip(l.voiced, l.voiceApproved != null, l.voiceApproved != null && l.voiceApproved !== l.defaultVoice,
      '♪', (l.voiced ? 'Voiced: in the topic file and generated in Phase 2.' : 'Not voiced.') + (l.voiceApproved == null ? ' (default)' : ' (your choice)') + ' Click to switch.',
      () => act(l, 'voice', !l.voiced));
    const f = plugin.isTopicFile ? null : chip(l.inFix, l.fixApproved != null, l.fixApproved != null && l.fixApproved !== l.defaultFix,
      'Fix', !l.changed ? 'The text is unchanged, so nothing goes into the DBVO Fix plugin.' : (l.inFix ? 'Your edit goes into the DBVO Fix plugin (the subtitle changes in game).' : 'Your edit is NOT written to the DBVO Fix plugin.') + (l.fixApproved == null ? ' (default)' : ' (your choice)') + ' Click to switch.',
      () => { if (l.changed) act(l, 'fix', !l.inFix); });
    if (f && !l.changed) f.disabled = true;
    return h('div.row', { style: { gap: '4px' } }, v, f);
  }

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
      notvoiced: L.filter((l) => !l.voiced && l.status !== 'Removed' && !l.excluded && l.kind !== 'SkippedEditorId').length,
      stage: L.filter((l) => l.stage && live(l)).length,
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
        case 'notvoiced': if (!(!l.voiced && l.status !== 'Removed' && !l.excluded && l.kind !== 'SkippedEditorId')) return false; break;
        case 'stage': if (!(l.stage && live(l))) return false; break;
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
      h('button.btn.ghost.sm', { onclick: () => go('mods') }, '← Mods'),
      h('h1', mod.name),
      mod.version ? h('span.sub', 'v' + mod.version) : null,
      h('button.icon-btn', { title: 'Rename / version', onclick: editMeta }, '✎'),
      h('span.faint', 'Mod Category'),
      categorySelect(mod.category, async (c) => { try { await rpc('mod.setCategory', { id, category: c }); mod.category = c; await refreshState(); renderHead(); } catch (e) { fail(e); } }),
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
          m.nexusSearchUrl ? h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url: m.nexusSearchUrl }); } }, 'find on Nexus') : h('span.faint', '(set the base game folder in Settings)'))),
        h('div.spacer'),
        h('button.btn.sm', { title: 'Upload the master files this plugin needs so its lines can be read', onclick: uploadMasters }, 'Upload masters…')));
    }
    for (const n of plugin.notices) banners.append(h('div.banner.bad', h('span', n), h('div.spacer'), h('button.btn.sm', { title: 'Add the .strings files of a localized plugin', onclick: uploadStrings }, 'Add strings files…')));
    if (mod.uploadedMasters.length) {
      banners.append(h('div.banner.info', h('span', `Uploaded masters kept in this mod: ${mod.uploadedMasters.join(', ')}`), h('div.spacer'),
        h('button.btn.sm', { onclick: async () => { if (await confirmBox('Delete uploaded masters?', 'They are only needed for reading the plugin. You can upload them again for the next update.', 'Delete', true)) { await rpc('mod.deleteMasters', { id }); reload(); } } }, 'Delete to save space')));
    }
  }

  function renderTabs() {
    clear(tabs);
    if (!mod.plugins.length) { tabs.classList.add('hidden'); return; }
    tabs.classList.remove('hidden');
    const label = (p) => `${p.fileName}  ·  ${p.isTopicFile ? 'topic file' : p.needsFix ? `fix ${p.fixes}` : 'clean'}${p.unreviewed ? `  ·  ${p.unreviewed} to review` : ''}`;
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
    const chip = (key, label) => h('span.chip' + (filter === key ? '.on' : ''), { onclick: () => { filter = key; save(); renderFilters(); grid.setRows(visibleLines()); } }, label, h('span.n', c[key]));
    clear(filters);
    if (!plugin) return;
    const q = h('input', { type: 'text', placeholder: 'Search text, FormID, EditorID…', value: search });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visibleLines()); }, 150));
    filters.append(chip('all', 'All'), chip('fixes', 'In DBVO Fix'), chip('notvoiced', 'Not voiced'), chip('flagged', 'Flagged'), chip('unreviewed', 'Not reviewed'),
      c.stage ? chip('stage', 'Stage directions') : null,
      c.player ? chip('player', 'Player names') : null,
      chip('changed', 'Changed in mod'), chip('edited', 'Edited by you'), chip('hidden', 'Hidden by prompts'), chip('excluded', 'Excluded'), chip('removed', 'Removed'),
      h('div.spacer'), q, columnsMenu(grid),
      h('button.btn.sm', { title: 'Show or hide the editing panel', onclick: () => { showInsp = !showInsp; store.set('insp1', showInsp); split.classList.toggle('no-insp', !showInsp); renderFilters(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
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
      h('button.btn.sm.ghost', { onclick: () => grid.selectAll(!sel.length) }, sel.length ? 'Clear selection' : 'Select all shown'),
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
      return h('div.help', { style: { borderColor: '#5c4615', background: '#2b2111', color: '#f2dfb0' } },
        h('p', h('b', 'Player name line. '), 'The subtitle shows the player\'s chosen name where <Alias=Player> is, so it can\'t be voiced in this patch. DBVO modders voice these in separate player-name patches: one topic file per name, with the alias replaced by that name.'),
        h('p', 'Export the line below. It is saved as a topic .txt in your ', h('b', 'Downloads'), ' folder; add it to your master player-name topic file. More player-name lines to patch are collected on the ', h('b', 'DBVO Repository'), ' mod page on Nexus Mods.'),
        l.needsOverride ? h('p', 'The script also reformatted this line, so the formatted version goes into the DBVO Fix plugin.') : null);
    }
    if (l.stage && (l.kind === 'Broken' || l.kind === 'TooLong')) return null; // the approval panel explains stage directions
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
        if (r.reasons && r.reasons.length) {
          checkEl.append(h('div.check.bad', h('b', 'Not accepted by the script yet:'), h('ul', { style: { margin: '4px 0 0', paddingLeft: '18px' } }, r.reasons.map((x) => h('li', x))),
            r.fix && r.fix !== text.trim() ? h('div', { style: { marginTop: '6px' } }, h('button.btn.sm', { title: 'Replace the text with what the script would make of it', onclick: () => { finalBox.value = r.fix; act(l, 'setFinal', r.fix); } }, 'Use the script\'s version: ' + r.fix)) : null));
        } else if (r.stage) checkEl.append(h('div.check.ok', '✓ A stage direction: choose below whether it is voiced or stays silent.'));
        else if (r.problem) checkEl.append(h('div.check.bad', r.problem));
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
          l.suggested && l.suggested !== l.final ? h('button.btn.sm', { title: 'Replace the text with the script\'s suggestion', onclick: () => act(l, 'accept') }, 'Use suggestion') : null,
          l.final != null && l.final !== l.original ? h('button.btn.sm', { title: 'Undo your edit and use the in-game text', onclick: () => act(l, 'revert') }, 'Back to original') : null,
          h('div.spacer'),
          h('button.btn.sm', { title: l.excluded ? 'Bring this line back' : 'Leave this line out of the topic file and DBVO Fix plugin', onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? 'Include' : 'Exclude'))),
      approvalPanel(l),
      l.isPlayerName ? h('div.row', h('button.btn.primary.sm', { onclick: () => exportPlayerNames([l.key]) }, 'Export this line to Downloads'),
        l.playerNameExported ? h('span.badge.b-valid', 'exported') : null) : null,
      l.hasOtherAlias && !l.isPlayerName ? variantsEditor(l) : null,
      l.hiddenByPrompts ? h('label.row', h('input', { type: 'checkbox', checked: l.includeAnyway, onchange: (e) => act(l, 'includeHidden', e.target.checked) }),
        'Include anyway (every response in this plugin has its own prompt, so the game normally never shows this text)') : null,
      l.status === 'ChangedInMod' ? h('div.help.warn', h('p', h('b', 'The mod changed this line.')),
        h('p', h('span.dim', 'Was: '), l.previousOriginal || '—'), h('p', h('span.dim', 'Your earlier final: '), l.previousFinal || '—'),
        h('div.row', h('button.btn.sm', { title: 'The mod update changed this line; keep your edit', onclick: () => act(l, 'keepPrevious') }, 'Keep my earlier edit'), h('button.btn.sm', { title: 'Drop your edit and use the updated text', onclick: () => act(l, 'acceptChange') }, 'Use the new text'))) : null,
    );
    if (!l.reviewed && l.flagged) act(l, 'review');
  }

  function variantsEditor(l) {
    const ta = h('textarea', { rows: 4, placeholder: 'One line per variant, e.g.\nAlvor sent me. Riverwood is in danger.\nGerdur sent me. Riverwood is in danger.' }, (l.variants || []).join('\n'));
    return h('div', h('div.field-label', 'Written-out variants (each becomes its own topic line and file)'), ta,
      h('div.row', { style: { marginTop: '6px' } }, h('span.faint.grow', 'Write each exactly as the subtitle will read in game. The plugin is not changed.'),
        h('button.btn.sm', { title: 'Extra spellings of this line that also go into the topic file', onclick: () => act(l, 'variants', ta.value.split('\n')) }, 'Save variants')));
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
