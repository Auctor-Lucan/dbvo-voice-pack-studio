// Phase 1: Mods list and the per-plugin workspace.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, droppedPaths } from './lib.js';
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
  const [t, c] = KIND_LABEL[l.kind] || [l.kind, 'b-skip'];
  return h('span.badge.' + c, t);
}

// ------------------------------------------------------------------ list

export async function modsView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'Mods'),
    h('span.sub', 'Phase 1 · parse plugins, fix player lines, write DBVO Fix plugins. Shared by every voice pack.'),
    h('div.spacer'),
    h('button.btn.primary', { onclick: newMod }, '+ New mod')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.mods.length) {
    body.append(h('div.empty', h('h2', 'No mods yet'),
      h('div', 'Create a mod, then drop its .esp / .esm files into it.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { onclick: newMod }, '+ New mod'))));
    return view;
  }
  body.append(h('div.cards', s.mods.map((m) => h('div.card', { onclick: () => go('mod', { id: m.id }) },
    h('div.row', h('h3.grow', m.name), m.version ? h('span.dim', 'v' + m.version) : null),
    m.plugins.length ? m.plugins.map((p) => h('div.row.meta',
      h('span.mono.grow', p.fileName),
      h('span', `${p.topicLines} lines`),
      p.needsFix ? h('span.badge.b-fixed', `${p.fixes} fixes`) : h('span.badge.b-valid', 'no fix needed'),
      p.unreviewed ? h('span.badge.b-broken', `${p.unreviewed} to review`) : null))
      : h('div.meta', 'No plugins yet')))));
  return view;
}

async function newMod() {
  const r = await prompt('New mod', [
    { name: 'name', label: 'Mod name', placeholder: 'e.g. Legacy of the Dragonborn' },
    { name: 'version', label: 'Mod version (optional)', placeholder: 'e.g. 6.10.1' },
  ]);
  if (!r || !r.name.trim()) return;
  try {
    const m = await rpc('mod.create', r);
    go('mod', { id: m.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ workspace

export async function modView(id) {
  let mod = await rpc('mod.get', { id });
  const ui = store.get('mod:' + id, { plugin: null, filter: 'all' });
  let plugin = mod.plugins.find((p) => p.fileName === ui.plugin) || mod.plugins[0] || null;
  let filter = ui.filter || 'all';
  let search = '';
  let focused = null;

  const view = h('div.view');
  const head = h('div.view-head');
  const banners = h('div');
  const tabs = h('div.filters');
  const filters = h('div.filters');
  const split = h('div.split');
  const left = h('div.left');
  const insp = h('div.inspector');
  split.append(left, insp);
  const actions = h('div.actionbar');
  view.append(head, banners, tabs, filters, split, actions);

  const grid = new VGrid(left, {
    rowKey: (l) => l.key,
    rowClass: (l) => (l.excluded || l.status === 'Removed' || (l.hiddenByPrompts && !l.includeAnyway) || l.kind === 'SkippedEditorId' ? 'muted' : ''),
    onRowClick: (l) => { focused = l; renderInspector(); },
    columns: [
      { title: '', width: 26, render: (l) => h('input', { type: 'checkbox', checked: grid.selected.has(l.key), onchange: () => grid.toggle(l.key) }) },
      { title: 'Type', width: 150, render: (l) => h('div.row', kindBadge(l),
        l.status === 'ChangedInMod' ? h('span.badge.b-changed', 'changed in mod') : null,
        l.hiddenByPrompts ? h('span.badge.b-hidden', { title: 'Every response has its own prompt, so the game never shows this topic text.' }, l.includeAnyway ? 'hidden · included' : 'hidden') : null) },
      { title: 'FormID', width: 80, cls: 'mono', render: (l) => l.formId },
      { title: 'Record', width: 88, cls: 'dim', render: (l) => l.record },
      { title: 'Original (plugin)', flex: 1, render: (l) => h('span', { title: l.original }, l.original) },
      { title: 'Final in-game line', flex: 1.2, render: (l) => finalInput(l) },
      { title: '', width: 92, render: (l) => h('div.row',
        l.suggested && l.final !== l.suggested ? h('button.icon-btn', { title: 'Use the script\'s suggestion', onclick: () => act(l, 'accept') }, '✓') : null,
        l.final !== l.original ? h('button.icon-btn', { title: 'Undo: back to the original text', onclick: () => act(l, 'revert') }, '↶') : null,
        h('button.icon-btn', { title: l.excluded ? 'Include again' : 'Exclude from the topic file', onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? '⊕' : '⊘')) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  function finalInput(l) {
    if (l.status === 'Removed') return h('span.faint', l.final || '');
    const inp = h('input', { type: 'text', value: l.final ?? '', placeholder: l.kind === 'Broken' || l.kind === 'TooLong' ? 'Fix this line, or leave empty' : '' });
    if (l.needsOverride) inp.style.color = 'var(--info)';
    inp.addEventListener('change', () => act(l, 'setFinal', inp.value));
    inp.addEventListener('focus', () => { focused = l; renderInspector(); grid.focus(l.key); });
    return inp;
  }

  async function act(l, action, value) {
    try {
      const r = await rpc('line1.update', { modId: mod.id, plugin: plugin.fileName, key: l.key, action, value });
      Object.assign(l, r.line);
      Object.assign(plugin, r.plugin);
      grid.refreshRow(l.key);
      if (focused && focused.key === l.key) renderInspector();
      renderTabs();
      renderActions();
    } catch (e) { fail(e); }
  }

  async function reload() {
    mod = await rpc('mod.get', { id });
    plugin = mod.plugins.find((p) => p.fileName === (plugin && plugin.fileName)) || mod.plugins[0] || null;
    renderAll();
  }

  function counts() {
    const L = plugin ? plugin.lines : [];
    return {
      all: L.length,
      fixes: L.filter((l) => l.needsOverride).length,
      flagged: L.filter((l) => l.flagged && l.status !== 'Removed' && !l.excluded).length,
      unreviewed: L.filter((l) => l.flagged && !l.reviewed && l.status !== 'Removed' && !l.excluded).length,
      changed: L.filter((l) => l.status === 'ChangedInMod').length,
      hidden: L.filter((l) => l.hiddenByPrompts).length,
      excluded: L.filter((l) => l.excluded).length,
      removed: L.filter((l) => l.status === 'Removed').length,
      edited: L.filter((l) => l.userEdited).length,
    };
  }

  function visibleLines() {
    if (!plugin) return [];
    const q = search.toLowerCase();
    return plugin.lines.filter((l) => {
      switch (filter) {
        case 'fixes': if (!l.needsOverride) return false; break;
        case 'flagged': if (!(l.flagged && l.status !== 'Removed' && !l.excluded)) return false; break;
        case 'unreviewed': if (!(l.flagged && !l.reviewed && l.status !== 'Removed' && !l.excluded)) return false; break;
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
    clear(head).append(
      h('button.btn.ghost.sm', { onclick: () => go('mods') }, '← Mods'),
      h('h1', mod.name),
      mod.version ? h('span.sub', 'v' + mod.version) : null,
      h('button.icon-btn', { title: 'Rename / version', onclick: editMeta }, '✎'),
      h('div.spacer'),
      h('button.btn', { onclick: addPlugins }, '+ Add plugins'),
      h('button.btn', { onclick: exportOutputs, disabled: !plugin }, 'Write topic files + DBVO Fix plugins…'),
      h('button.btn.ghost.danger', { onclick: delMod }, 'Delete'));
  }

  function renderBanners() {
    clear(banners);
    if (!mod.plugins.length) {
      const dz = h('div.dropzone', { style: { margin: '18px' } }, 'Drop .esp / .esm / .esl files here, or ', h('a', { href: '#', onclick: (e) => { e.preventDefault(); addPlugins(); } }, 'choose files'), '.');
      wireDrop(dz, importPaths);
      banners.append(dz);
      return;
    }
    if (!plugin) return;
    if (plugin.missingMasters.length) {
      banners.append(h('div.banner.warn',
        h('b', 'Missing masters:'),
        plugin.missingMasters.map((m) => h('span.row', h('span.mono', m.fileName),
          m.nexusSearchUrl ? h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url: m.nexusSearchUrl }); } }, 'find on Nexus') : h('span.faint', '(base game: set the base game folder in Settings)'))),
        h('div.spacer'),
        h('button.btn.sm', { onclick: uploadMasters }, 'Upload masters…'),
        h('span.faint', 'Needed to judge which topic text the game shows; parsing still works without them.')));
    }
    for (const n of plugin.notices) {
      banners.append(h('div.banner.bad', h('span', n), h('div.spacer'), h('button.btn.sm', { onclick: uploadStrings }, 'Add strings files…')));
    }
    if (mod.uploadedMasters.length) {
      banners.append(h('div.banner.info', h('span', `Uploaded masters kept in this mod: ${mod.uploadedMasters.join(', ')}`), h('div.spacer'),
        h('button.btn.sm', { onclick: async () => { if (await confirmBox('Delete uploaded masters?', 'They are only needed for parsing. You can upload them again for the next update.', 'Delete', true)) { await rpc('mod.deleteMasters', { id }); reload(); } } }, 'Delete to save space')));
    }
  }

  function renderTabs() {
    clear(tabs);
    if (!mod.plugins.length) { tabs.classList.add('hidden'); return; }
    tabs.classList.remove('hidden');
    for (const p of mod.plugins) {
      tabs.append(h('span.chip' + (p === plugin ? '.on' : ''), {
        onclick: () => { plugin = p; save(); renderAll(); },
      }, h('span.mono', p.fileName), p.needsFix ? h('span.n', '· fix') : h('span.n', '· clean'), p.unreviewed ? h('span.badge.b-broken', p.unreviewed) : null));
    }
    const drop = h('span.chip', { title: 'Drop a newer version of a plugin here to update it' }, '+ drop plugin / update');
    wireDrop(drop, importPaths);
    drop.addEventListener('click', addPlugins);
    tabs.append(drop);
  }

  function renderFilters() {
    const c = counts();
    const chip = (key, label) => h('span.chip' + (filter === key ? '.on' : ''), { onclick: () => { filter = key; save(); renderFilters(); grid.setRows(visibleLines()); } }, label, h('span.n', c[key]));
    clear(filters);
    if (!plugin) return;
    const q = h('input', { type: 'text', placeholder: 'Search text, FormID, EditorID…', value: search });
    q.addEventListener('input', () => { search = q.value; grid.setRows(visibleLines()); });
    filters.append(chip('all', 'All'), chip('fixes', 'In DBVO Fix'), chip('flagged', 'Flagged'), chip('unreviewed', 'Not reviewed'),
      chip('changed', 'Changed in mod'), chip('edited', 'Edited by you'), chip('hidden', 'Hidden by prompts'), chip('excluded', 'Excluded'), chip('removed', 'Removed'),
      h('div.spacer'), q);
  }

  function renderActions() {
    clear(actions);
    if (!plugin) return;
    const sel = [...grid.selected];
    const c = counts();
    actions.append(
      h('span.dim', `${plugin.topicLines} topic lines · `, plugin.needsFix ? h('span', { style: { color: 'var(--info)' } }, `${plugin.fixFileName} needed (${c.fixes} lines)`) : h('span', { style: { color: 'var(--ok)' } }, 'no DBVO Fix plugin needed')),
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

  function renderInspector() {
    clear(insp);
    const l = focused && plugin && plugin.lines.find((x) => x.key === focused.key);
    if (!l) {
      insp.append(h('h3', 'Line'), h('div.dim', 'Click a row to see it in full.'),
        h('div.faint', { style: { marginTop: '12px' } },
          h('div', h('b', 'Fixed'), ' rows are reformatted by the script and go into the DBVO Fix plugin.'),
          h('div', h('b', 'BROKEN LINE'), ' rows (such as <Alias=…> placeholders or *) need your decision: reword the line, or add written-out variants as extra topic lines.'),
          h('div', h('b', 'TOO LONG WILL CTD'), ' rows are over 150 characters.'),
          h('div', 'Unresolved flagged rows get no override, so the game keeps its original line.')));
      return;
    }
    const finalBox = h('textarea', { rows: 3 }, l.final ?? '');
    finalBox.addEventListener('change', () => act(l, 'setFinal', finalBox.value));
    insp.append(
      h('div.row', kindBadge(l), h('span.mono.dim', l.formId), h('span.dim', l.record), h('div.spacer'), l.editorId ? h('span.faint.mono', l.editorId) : null),
      h('div', h('div.field-label', 'Original (in the plugin)'), h('div.box', l.original)),
      l.suggested && l.suggested !== l.original ? h('div', h('div.field-label', 'Script suggestion'), h('div.box.suggest', l.suggested)) : null,
      h('div', h('div.field-label', 'Final in-game line (topic file + DBVO Fix plugin)'), finalBox,
        h('div.row', { style: { marginTop: '6px' } },
          l.suggested ? h('button.btn.sm', { onclick: () => act(l, 'accept') }, 'Use suggestion') : null,
          h('button.btn.sm', { onclick: () => act(l, 'revert') }, 'Back to original'),
          h('div.spacer'),
          h('button.btn.sm', { onclick: () => act(l, 'exclude', !l.excluded) }, l.excluded ? 'Include' : 'Exclude'))),
      l.needsOverride ? h('div.banner.info', { style: { margin: 0 } }, 'This text is written to ' + plugin.fixFileName + '.') : null,
      l.hasPlaceholder || l.kind === 'Broken' ? variantsEditor(l) : null,
      l.hiddenByPrompts ? h('label.row', h('input', { type: 'checkbox', checked: l.includeAnyway, onchange: (e) => act(l, 'includeHidden', e.target.checked) }),
        'Include anyway (every response in this plugin has its own prompt, so the game normally never shows this text)') : null,
      l.status === 'ChangedInMod' ? h('div.banner.warn', { style: { margin: 0, flexDirection: 'column', alignItems: 'stretch' } },
        h('b', 'The mod changed this line'),
        h('div', h('span.dim', 'Was: '), l.previousOriginal || '—'),
        h('div', h('span.dim', 'Your earlier final: '), l.previousFinal || '—'),
        h('div.row', h('button.btn.sm', { onclick: () => act(l, 'keepPrevious') }, 'Keep my earlier edit'), h('button.btn.sm', { onclick: () => act(l, 'acceptChange') }, 'Use the new text'))) : null,
    );
    if (!l.reviewed && l.flagged) act(l, 'review');
  }

  function variantsEditor(l) {
    const ta = h('textarea', { rows: 4, placeholder: 'One line per variant, e.g.\nAlvor sent me. Riverwood is in danger.\nGerdur sent me. Riverwood is in danger.' }, (l.variants || []).join('\n'));
    return h('div', h('div.field-label', 'Written-out variants (each becomes its own topic line and file)'), ta,
      h('div.row', { style: { marginTop: '6px' } }, h('span.faint.grow', 'Use "..." rather than "…". The plugin is not changed by variants.'),
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
      toast('Reading plugins…');
      const r = await rpc('mod.import', { id: mod.id, paths });
      mod = r.mod;
      const last = r.reports[r.reports.length - 1];
      plugin = mod.plugins.find((p) => p.fileName === last.plugin) || mod.plugins[0];
      renderAll();
      const lines = r.reports.map((x) => x.changed || x.removed || x.kept
        ? `${x.plugin}: update — ${x.kept} kept, ${x.changed} changed, ${x.added} new, ${x.removed} removed`
        : `${x.plugin}: ${x.added} lines read`);
      toast(lines.join(' · '));
    } catch (e) { fail(e); }
  }

  async function addPlugins() {
    const paths = await rpc('pick.files', { title: 'Choose plugins', filter: 'Skyrim plugins|*.esp;*.esm;*.esl' });
    if (paths) importPaths(paths);
  }

  async function uploadMasters() {
    const paths = await rpc('pick.files', { title: 'Choose master plugins', filter: 'Skyrim plugins|*.esp;*.esm;*.esl' });
    if (!paths) return;
    await rpc('mod.addMasters', { id, paths });
    if (await confirmBox('Masters added', 'Read the plugin again now so the masters are used?', 'Re-read')) {
      const stored = await rpc('state');
      importPaths(mod.plugins.map((p) => p.fileName).length ? await pluginStoredPaths() : []);
    } else reload();
  }

  async function pluginStoredPaths() {
    // Re-import from the copies kept in the library.
    return mod.plugins.map((p) => `${app.state.libraryRoot}\\mods\\${mod.id}\\plugins\\${p.fileName}`);
  }

  async function uploadStrings() {
    const paths = await rpc('pick.files', { title: 'Choose the plugin\'s strings files', filter: 'Strings|*.strings;*.dlstrings;*.ilstrings' });
    if (!paths) return;
    await rpc('mod.addStrings', { id, paths });
    importPaths(await pluginStoredPaths());
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
      await modal('Files written', [h('div.box.mono', written.join('\n'))]);
    } catch (e) { fail(e); }
  }

  async function toPhase2() {
    const unreviewed = mod.plugins.flatMap((p) => p.lines.filter((l) => l.flagged && !l.reviewed && l.status !== 'Removed' && !l.excluded).map((l) => ({ p, l })));
    if (unreviewed.length) {
      const goReview = await modal('Flagged lines not reviewed', [
        h('div', `${unreviewed.length} BROKEN LINE / TOO LONG WILL CTD row(s) were never opened. Unresolved rows get no override, so the game keeps the original text (which may be silent or crash for TOO LONG lines).`),
        h('div.box', { style: { maxHeight: '200px', overflow: 'auto' } }, unreviewed.slice(0, 40).map(({ p, l }) => h('div.row', kindBadge(l), h('span.mono.faint', p.fileName), h('span', l.original)))),
      ], [{ label: 'Continue anyway', value: false }, { label: 'Show them', primary: true, value: true }]);
      if (goReview === null) return;
      if (goReview) {
        plugin = unreviewed[0].p;
        filter = 'unreviewed';
        save();
        renderAll();
        return;
      }
    }
    await refreshState();
    const existing = app.state.patches.filter((x) => x.modIds.includes(mod.id));
    if (existing.length === 1) { go('patch', { id: existing[0].id }); return; }
    go('patches', { newFor: mod.id });
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
