// Phase 3: FOMOD Creation — import any ModuleConfig.xml, options from DBVO Patches, the DBVO JSON, validate, simulate, build.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce, switchEl, segmented, popupMenu, helpButton, keepScroll, dragSource, dragTarget, closeAllModals, withBusy } from './lib.js';
import { app, go, refreshState } from './app.js';
import { voiceChip } from './phase2.js';

const GROUP_TYPES = [
  ['SelectAny', 'Select any', 'Users tick as many as they like. The usual choice for patch lists.'],
  ['SelectAll', 'Select all (always installed)', 'Everything in the group is installed; users can\'t untick. For core files and information pages.'],
  ['SelectExactlyOne', 'Select exactly one', 'Radio buttons: one must be picked.'],
  ['SelectAtMostOne', 'Select at most one', 'Radio buttons, or none.'],
  ['SelectAtLeastOne', 'Select at least one', 'Tick one or more.'],
];
const MODES = [
  ['Detect', 'Recommended when the mod is installed', 'Detects the mod\'s plugins: pre-ticked when they are active, greyed out (Not usable) when they are not. The usual DBVO setup.'],
  ['Required', 'Required', 'Always installed; users can\'t untick it.'],
  ['Recommended', 'Recommended (always pre-ticked)', 'Pre-ticked for everyone, no detection.'],
  ['Optional', 'Optional', 'Not ticked; the user decides.'],
  ['Manual', 'Manual (no detection)', 'For Creation Club or content that can\'t be detected. Written as Optional, so the user ticks it by hand.'],
  ['Advanced', 'Advanced (custom XML)', 'Any type descriptor, kept exactly as written: flags, several patterns, CouldBeUsable…'],
];

// ------------------------------------------------------------------ list

export async function fomodsView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'FOMOD Creation'),
    helpButton('1-start-a-project'),
    h('span.sub', 'Build the installer: pages of options that install each mod\'s .fuz files and DBVO Fix plugins, plus the DBVO voice pack JSON.'),
    h('div.spacer'),
    h('button.btn', { title: 'Read an existing FOMOD folder or archive into an editable project', onclick: () => importFomod() }, 'Import FOMOD…'),
    h('button.btn.primary', { title: 'Start a FOMOD installer from your DBVO Patches', onclick: newFomod }, '+ New FOMOD')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.fomods.length) {
    body.append(h('div.empty', h('h2', 'No FOMOD projects yet'),
      h('div', 'Start a new one and add your DBVO Patches, or import an existing ModuleConfig.xml (from FOMOD Creation Tool or anywhere else) to keep working on it.'),
      h('div.row', { style: { justifyContent: 'center', marginTop: '14px' } }, h('button.btn', { title: 'Read an existing FOMOD folder or archive into an editable project', onclick: () => importFomod() }, 'Import FOMOD…'), h('button.btn.primary', { title: 'Start a FOMOD installer from your DBVO Patches', onclick: newFomod }, '+ New FOMOD'))));
    return view;
  }
  body.append(h('div.cat', h('div.cat-head', h('h3', 'FOMOD projects'), h('span.faint', s.fomods.length)),
    h('div.cards2', s.fomods.map((f) => {
      const v = s.voicePacks.find((x) => x.id === f.voicePackId);
      return h('div.pcard', { onclick: () => go('fomod', { id: f.id }) },
        h('div.row', h('span.title.grow.ellipsis', f.name), voiceChip(f.voicePack || '—', v && v.color)),
        h('div.stat-row', h('span', h('b', f.steps), ' pages'), h('span', h('b', f.options), ' options'),
          h('span', f.lastBuildUtc ? 'built ' + new Date(f.lastBuildUtc).toLocaleString() : 'not built yet')),
        f.lastArchive ? h('div.mono.faint.ellipsis', f.lastArchive) : null);
    }))));
  return view;
}

async function newFomod() {
  const s = app.state;
  if (!s.voicePacks.length) { toast('Create a voice pack first, in Global Settings → Voice packs.', true); go('settings', { tab: 'voices', id: 'new' }); return; }
  const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
  const name = h('input', { type: 'text', placeholder: 'e.g. DBVO MyVoice' });
  const root = h('input', { type: 'text', placeholder: 'the final folder of your patches, e.g. D:\\DBVO MyVoice FOMOD' });
  // The root follows the voice pack until you type your own: switching voice packs updates a root it filled in.
  let autoRoot = '';
  const pickVoice = () => {
    const v = s.voicePacks.find((x) => x.id === voice.value);
    if (v && (!root.value || root.value === autoRoot)) root.value = autoRoot = (v.destinations.finalRoot || '').replace(/\\?[^\\]*\{mod\}.*$/, '');
  };
  voice.addEventListener('change', pickVoice);
  pickVoice();
  const ok = await modal('New FOMOD', [
    h('div.col', h('div.field-label', 'Voice pack'), voice),
    h('div.col', h('div.field-label', 'Name'), name),
    h('div.col', h('div.field-label', 'FOMOD root folder'), h('div.path-input', root, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title: 'FOMOD root folder' }); if (f) root.value = f; } }, 'Browse')),
      h('div.faint', 'The folder that holds your patch folders, images and so on. File sources in the installer are relative to it.')),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }], { help: '1-start-a-project' });
  if (!ok) return;
  try {
    const r = await rpc('fomod.create', { voicePackId: voice.value, name: name.value, rootFolder: root.value.trim() });
    go('fomod', { id: r.project.id });
  } catch (e) { fail(e); }
}

async function importFomod(into) {
  const s = app.state;
  const path = await rpc('pick.file', { title: 'Choose ModuleConfig.xml (in the fomod folder)', filter: 'ModuleConfig.xml|ModuleConfig.xml;*.xml' });
  if (!path) return;
  let voicePackId = '';
  if (!into) {
    const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
    const ok = await modal('Import FOMOD', [h('div', 'Everything is kept: pages, groups, options, descriptions, images, files, detection, flags, conditions and visibility.'),
      h('div.col', h('div.field-label', 'Voice pack this FOMOD is for'), voice)], [{ label: 'Cancel', value: false }, { label: 'Import', primary: true, value: true }]);
    if (!ok) return;
    voicePackId = voice.value;
  }
  try {
    const r = await rpc('fomod.import', { path, into: into || '', voicePackId });
    toast(`Imported ${r.project.project.steps.length} pages.` + (r.jsonFrom ? ` The DBVO JSON is already installed by "${r.jsonFrom}", so the app does not add another.` : ''));
    go('fomod', { id: r.project.project.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ editor

// The open editor's job-done listener (and its save-on-leave): one at a time.
let offJobDone = null;

export async function fomodView(id) {
  await refreshState();
  let dto = await rpc('fomod.get', { id });
  let proj = dto.project;
  const voice = app.state.voicePacks.find((v) => v.id === proj.voicePackId);
  let selKey = store.get('fomodSel:' + id, 'project');
  const collapsed = store.get('fomodCollapsed:' + id, {});
  const savedEl = h('span.saved.ok', { title: 'Saved in DBVO Studio. Your FOMOD folder (and its ModuleConfig.xml) is not changed: Build ▾ writes the finished installer, with the new XML, to a folder in the release folder, named like the archive.' }, 'Saved ✓');

  const view = h('div.view.work');
  const head = h('div.view-head');
  const tree = h('div.ftree');
  const edit = h('div.fedit');
  view.append(head, h('div.fomod', tree, edit));

  // Saves go out one at a time, in order, half a second after the last change. Actions the backend saves itself
  // (Add DBVO Patches, Refresh linked, Build) first drop the pending save and wait for one in flight, so an older copy
  // can't land on disk after theirs.
  let saveTimer = 0, saving = Promise.resolve();
  // saveNow's promise rejects when the save failed (after saying so), so actions that need it saved stop there.
  let saveFailed = false;
  const saveNow = () => {
    clearTimeout(saveTimer); saveTimer = 0;
    const snapshot = JSON.parse(JSON.stringify(proj));
    const job = saving.then(async () => {
      try {
        await rpc('fomod.save', { project: snapshot });
        saveFailed = false;
        if (!saveTimer) { savedEl.textContent = 'Saved ✓'; savedEl.className = 'saved ok'; }
      } catch (e) { saveFailed = true; savedEl.textContent = 'Not saved'; savedEl.className = 'saved bad'; fail(e); e.shown = true; throw e; }
    });
    saving = job.catch(() => {});
    return job;
  };
  const persist = () => { clearTimeout(saveTimer); saveTimer = setTimeout(() => saveNow().catch(() => {}), 500); };
  // Before an action the backend saves itself: send the waiting save now (not drop it) and wait for every save.
  const settleSaves = async () => { if (saveTimer) await saveNow(); else { await saving; if (saveFailed) { const e = new Error('The last change could not be saved.'); e.shown = true; throw e; } } };
  // An error the save already showed isn't shown twice.
  const failOnce = (e) => { if (!e || !e.shown) fail(e); };
  // Leaving the editor (another page, closing the app) sends a waiting save straight away.
  const flushOnLeave = () => { if (saveTimer) saveNow().catch(() => {}); };
  window.addEventListener('pagehide', flushOnLeave);
  window.addEventListener('beforeunload', flushOnLeave);
  const changed = (rerenderTree = false) => { savedEl.textContent = 'Saving…'; savedEl.className = 'saved'; persist(); if (rerenderTree) renderTree(); };
  // A change to the structure (order, pages, groups): the tree and the open panel both show it.
  const restructure = () => { changed(); renderTree(); renderEditor(); };

  // ---- lookup helpers
  const find = (key) => {
    if (key === 'project' || key === 'json') return { kind: key };
    for (const st of proj.steps) {
      if (st.id === key) return { kind: 'step', step: st };
      for (const g of st.groups) {
        if (g.id === key) return { kind: 'group', step: st, group: g };
        for (const o of g.options) if (o.id === key) return { kind: 'option', step: st, group: g, option: o };
      }
    }
    return { kind: 'project' };
  };
  const newId = () => Math.random().toString(36).slice(2, 10);
  const select = (key) => { selKey = key; store.set('fomodSel:' + id, key); renderTree(); renderEditor(); };
  // Moves look the item up when clicked (not when the panel was drawn), so clicking ↑ again keeps going up.
  // Which setting orders a list: the pages (stepsOrder), a page's groups (groupsOrder) or a group's options (order).
  const orderOf = (arr) => {
    if (arr === proj.steps) return [proj, 'stepsOrder'];
    for (const st of proj.steps) { if (st.groups === arr) return [st, 'groupsOrder']; for (const g of st.groups) if (g.options === arr) return [g, 'order']; }
    return [null, null];
  };
  const byName = (a, b) => (a.name || '').localeCompare(b.name || '', undefined, { sensitivity: 'base' });
  // A list the installer sorts A–Z or Z–A ignores its stored order, so moving something in it would change nothing in
  // MO2 or Vortex. Before the first move it takes the order the installer showed, and keeps your order from then on.
  const makeExplicit = (arr) => {
    const [owner, key] = orderOf(arr);
    if (!owner || !owner[key] || owner[key] === 'Explicit') return;
    arr.sort(owner[key] === 'Descending' ? (a, b) => byName(b, a) : byName);
    owner[key] = 'Explicit';
    toast(`${key === 'stepsOrder' ? 'Pages' : key === 'groupsOrder' ? 'Groups on this page' : 'Options in this group'} were sorted by name in the installer; they now keep the order you set.`);
  };
  const move = (arr, item, d) => {
    makeExplicit(arr);
    const i = arr.indexOf(item); if (i < 0) return;
    const j = d === 'top' ? 0 : d === 'bottom' ? arr.length - 1 : i + d;
    if (j < 0 || j >= arr.length || j === i) return;
    arr.splice(i, 1); arr.splice(j, 0, item); restructure();
  };
  const moveButtons = (arr, item, what) => {
    const i = arr.indexOf(item), last = arr.length - 1;
    return [
      h('button.btn.sm', { title: `Move this ${what} to the top`, disabled: i <= 0, onclick: () => move(arr, item, 'top') }, '⤒'),
      h('button.btn.sm', { title: 'Move up', disabled: i <= 0, onclick: () => move(arr, item, -1) }, '↑'),
      h('button.btn.sm', { title: 'Move down', disabled: i >= last, onclick: () => move(arr, item, 1) }, '↓'),
      h('button.btn.sm', { title: `Move this ${what} to the bottom`, disabled: i >= last, onclick: () => move(arr, item, 'bottom') }, '⤓'),
    ];
  };
  const blankGroup = () => ({ id: newId(), name: 'New group', type: 'SelectAny', order: 'Explicit', options: [] });
  const blankStep = () => ({ id: newId(), name: 'New page', visibleXml: null, groupsOrder: 'Explicit', groups: [blankGroup()] });
  // A new page at index at (the end when left out), then opened.
  const addStep = (at) => { makeExplicit(proj.steps); const st = blankStep(); proj.steps.splice(at ?? proj.steps.length, 0, st); changed(); select(st.id); };
  const addGroup = (st, at) => { if (at != null) makeExplicit(st.groups); const g = blankGroup(); st.groups.splice(at ?? st.groups.length, 0, g); if (collapsed[st.id]) { delete collapsed[st.id]; store.set('fomodCollapsed:' + id, collapsed); } changed(); select(g.id); };
  const deleteStep = async (st) => {
    if (!await confirmBox('Delete page?', `Delete "${st.name}" and its ${st.groups.reduce((a, g) => a + g.options.length, 0)} option(s)?`, 'Delete', true)) return;
    const i = proj.steps.indexOf(st); if (i < 0) return;
    proj.steps.splice(i, 1); changed();
    // What you had open stays open, unless it was on that page.
    select(find(selKey).kind !== 'project' || selKey === 'project' || selKey === 'json' ? selKey : proj.steps[Math.min(i, proj.steps.length - 1)]?.id || 'project');
  };
  const deleteGroup = async (st, g) => {
    if (!await confirmBox('Delete group?', `Delete "${g.name}" and its ${g.options.length} option(s)?`, 'Delete', true)) return;
    const i = st.groups.indexOf(g); if (i < 0) return;
    st.groups.splice(i, 1); changed();
    select(find(selKey).kind !== 'project' || selKey === 'project' || selKey === 'json' ? selKey : st.id);
  };

  function renderHead() {
    const build = h('button.btn.primary', { title: 'Write the finished installer, packed or as a folder' }, 'Build ▾');
    build.addEventListener('click', () => popupMenu(build, [
      { label: `Build and pack .${proj.archiveFormat}`, sub: `Puts the installer together, then writes the archive to ${proj.releaseFolder || '(set the release folder)'}`, onclick: () => runBuild(true) },
      { label: 'Build without packing', sub: `Writes the finished installer as a folder (new XML included) to ${proj.releaseFolder || '(set the release folder)'}, named like the archive: test it in MO2 / Vortex, or zip it yourself with 7-Zip`, onclick: () => runBuild(false) },
    ]));
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the list of FOMOD projects', onclick: () => go('fomods') }, '← FOMODs'),
      h('h1', proj.name || proj.info.name || 'FOMOD'),
      helpButton('3-edit-pages-groups-and-options'),
      voiceChip(dto.voiceName || '—', voice && voice.color),
      savedEl,
      h('div.spacer'),
      h('button.btn', { onclick: addPatches, title: 'Add an option per mod from your DBVO Patches, on the page of its category' }, '+ Add DBVO Patches…'),
      h('button.btn', { onclick: refreshLinked, title: 'Update the files of options linked to DBVO Patches (after changing folders or rebuilding .fuz)' }, '⟳ Refresh linked'),
      h('button.btn', { title: 'Check against the ModuleConfig 5.0 schema and for missing files', onclick: validate }, 'Validate'),
      h('button.btn', { title: 'Show what each option would be with a given plugin list', onclick: simulate }, 'Simulate install'),
      h('button.btn.ghost', { title: 'Show the generated info.xml and ModuleConfig.xml', onclick: previewXml }, 'View XML'),
      h('button.btn.ghost', { title: 'Replace this project with a FOMOD read from disk', onclick: () => importFomod(id) }, 'Re-import…'),
      build,
      h('button.btn.ghost.danger.sm', { title: 'Delete this FOMOD project (your files stay)', onclick: async () => { if (await confirmBox('Delete FOMOD project?', 'Only the project in this app is deleted; built archives and your files stay.', 'Delete', true)) { await rpc('fomod.delete', { id }); go('fomods'); } } }, 'Delete'));
  }

  // ---- tree
  let drag = null; // { option, fromGroup }
  function renderTree() {
    clear(tree);
    const node = (key, cls, label, extra, onclick) => {
      const n = h('div.fnode.' + cls + (selKey === key ? '.sel' : ''), { onclick: onclick || (() => select(key)) }, label, extra);
      return n;
    };
    tree.append(
      node('project', 'step', h('span', '⚙ Installer & info.xml'), null),
      node('json', 'step', h('span', '{ } DBVO voice pack JSON'), h('span.n', proj.json.enabled && (dto.existingJson || []).length ? '✓' : proj.json.enabled ? (proj.steps.some((s) => s.groups.some((g) => g.options.some((o) => o.carriesJson))) ? '✓' : '⚠') : 'off')));
    proj.steps.forEach((st, si) => {
      const open = !collapsed[st.id];
      const count = st.groups.reduce((a, g) => a + g.options.length, 0);
      const sn = node(st.id, 'step', h('span.row', h('span.faint', { onclick: (e) => { e.stopPropagation(); collapsed[st.id] = open; store.set('fomodCollapsed:' + id, collapsed); renderTree(); } }, open ? '▾' : '▸'), h('span.ellipsis', st.name || '(page)')),
        h('span.n', count));
      sn.title = 'Drag to reorder pages; right-click for more';
      dragSource(sn, { data: () => ({ step: st }), label: () => st.name || '(page)' });
      dragTarget(sn, {
        accepts: (d) => (d && d.step && d.step !== st ? `Put above ${st.name || 'this page'}` : d && d.group ? `Move to the end of ${st.name || 'this page'}` : null),
        onDrop: (d) => {
          if (d.step) { makeExplicit(proj.steps); proj.steps.splice(proj.steps.indexOf(d.step), 1); proj.steps.splice(proj.steps.indexOf(st), 0, d.step); }
          else { makeExplicit(st.groups); d.fromStep.groups.splice(d.fromStep.groups.indexOf(d.group), 1); st.groups.push(d.group); }
          restructure();
        },
      });
      sn.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        const i = proj.steps.indexOf(st), last = proj.steps.length - 1;
        popupMenu(sn, [
          { label: '+ Page above', onclick: () => addStep(proj.steps.indexOf(st)) },
          { label: '+ Page below', onclick: () => addStep(proj.steps.indexOf(st) + 1) },
          { label: '+ Group on this page', onclick: () => addGroup(st) },
          { label: 'Move to the top', disabled: i === 0, onclick: () => move(proj.steps, st, 'top') },
          { label: 'Move up', disabled: i === 0, onclick: () => move(proj.steps, st, -1) },
          { label: 'Move down', disabled: i === last, onclick: () => move(proj.steps, st, 1) },
          { label: 'Move to the bottom', disabled: i === last, onclick: () => move(proj.steps, st, 'bottom') },
          { label: 'Delete page…', onclick: () => deleteStep(st) },
        ]);
      });
      tree.append(sn);
      if (!open) return;
      st.groups.forEach((g) => {
        const gn = node(g.id, 'group', h('span.ellipsis', g.name || '(group)'), h('span.row', { style: { marginLeft: 'auto' } }, h('span.kind', (GROUP_TYPES.find((t) => t[0] === g.type) || [0, g.type])[1]), h('span.n', g.options.length)));
        dragSource(gn, { data: () => ({ group: g, fromStep: st }), label: () => g.name || '(group)' });
        dragTarget(gn, {
          accepts: (d) => (d && d.option ? `Move to the end of ${g.name || 'this group'}` : d && d.group && d.group !== g ? `Put above ${g.name || 'this group'}` : null),
          onDrop: (d) => {
            if (d.option) { makeExplicit(g.options); d.fromGroup.options.splice(d.fromGroup.options.indexOf(d.option), 1); g.options.push(d.option); }
            else { makeExplicit(st.groups); d.fromStep.groups.splice(d.fromStep.groups.indexOf(d.group), 1); st.groups.splice(st.groups.indexOf(g), 0, d.group); }
            restructure();
          },
        });
        gn.addEventListener('contextmenu', (e) => {
          e.preventDefault();
          popupMenu(gn, [
            { label: '+ Group above', onclick: () => addGroup(st, st.groups.indexOf(g)) },
            { label: '+ Group below', onclick: () => addGroup(st, st.groups.indexOf(g) + 1) },
            { label: '+ Option', onclick: () => { const o = blankOption(); g.options.push(o); changed(); select(o.id); } },
            { label: 'Delete group…', onclick: () => deleteGroup(st, g) },
          ]);
        });
        tree.append(gn);
        g.options.forEach((o) => {
          const badge = o.mode === 'Detect' ? h('span.kind', { title: 'Detects: ' + o.plugins.join(', ') }, 'detect')
            : h('span.kind', o.mode.toLowerCase());
          const on = node(o.id, 'opt', h('span.ellipsis', o.carriesJson ? '{ } ' : '', o.name || '(option)'), h('span.row', { style: { marginLeft: 'auto' } }, o.patchId ? h('span.kind', { title: 'Linked to a DBVO Patch' }, '🔗') : null, badge));
          dragSource(on, { data: () => ({ option: o, fromGroup: g }), label: () => o.name || '(option)' });
          dragTarget(on, {
            accepts: (d) => (d && d.option && d.option !== o ? `Put above ${o.name || 'this option'}` : null),
            onDrop: (d) => {
              makeExplicit(g.options);
              d.fromGroup.options.splice(d.fromGroup.options.indexOf(d.option), 1);
              g.options.splice(g.options.indexOf(o), 0, d.option);
              restructure();
            },
          });
          tree.append(on);
        });
      });
    });
    // With a page (or something on it) open, + Page goes right below that page; otherwise at the end.
    const at = (() => { const f = find(selKey); return f.step ? proj.steps.indexOf(f.step) + 1 : proj.steps.length; })();
    const endRow = h('div.row', { style: { padding: '10px 8px' } },
      h('button.btn.sm', { title: at < proj.steps.length ? `Add a page below “${proj.steps[at - 1].name}”` : 'Add a page at the end', onclick: () => addStep(at) }, at < proj.steps.length ? '+ Page below this one' : '+ Page'),
      h('span.faint', 'Drag pages, groups and options to reorder. Right-click a page or group for more.'));
    dragTarget(endRow, {
      accepts: (d) => (d && d.step ? 'Move to the end' : null),
      onDrop: (d) => { makeExplicit(proj.steps); proj.steps.splice(proj.steps.indexOf(d.step), 1); proj.steps.push(d.step); restructure(); },
    });
    tree.append(endRow);
  }

  // ---- editor panels
  const sec = (title, extra, ...rows) => h('div.section', h('div.section-head', h('h2', title), extra), h('div.section-body', rows));
  const srow = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', { title: typeof help === 'string' ? help : label }, control));
  const card = (label, help, control, o = {}) => {
    const c = h('div.scard' + (o.wide ? '.wide' : ''));
    const more = help && help.length > 70;
    c.append(h('div.scard-lbl', h('span', label),
      more ? h('button.qmark', { type: 'button', title: help, onclick: () => c.classList.toggle('open') }, '?') : null),
    control,
    help ? h('small.scard-help', { title: help }, help) : null);
    return c;
  };
  const txt = (obj, key, opts = {}) => {
    const i = opts.area ? h('textarea', { rows: opts.rows || 6, placeholder: opts.placeholder || '' }, obj[key] ?? '') : h('input', { type: 'text', value: obj[key] ?? '', placeholder: opts.placeholder || '', style: { width: '100%' } });
    i.addEventListener('input', () => { obj[key] = i.value; changed(opts.tree); if (opts.after) opts.after(); });
    return i;
  };
  // Description field with a small toolbar that inserts the icons at the caret.
  const ICONS = [['⚠️', 'Warning'], ['✅', 'Required / done'], ['⭐', 'Recommended'], ['ℹ️', 'Info'],
    ['❌', 'Not compatible'], ['⛔', 'Do not use'], ['❗', 'Important'], ['❓', 'Question / optional'],
    ['✏️', 'Edited / changed'], ['⚙️', 'Settings / setup'], ['⏳', 'Work in progress'], ['➡️', 'Next / see'],
    ['✨', 'New'], ['⚡', 'Performance'], ['❤️', 'Thanks'], ['☠️', 'Danger'], ['🔒', 'Mandatory / locked'], ['💾', 'Save / files']];
  const descArea = (obj, key, rows) => {
    const area = txt(obj, key, { area: true, rows });
    const insert = (ch) => {
      // Never clicked into: at the end, not the start.
      const focused = document.activeElement === area || area.__touched;
      const a = focused ? area.selectionStart : area.value.length, b = focused ? area.selectionEnd : a;
      area.setRangeText(ch, a, b, 'end');
      area.focus();
      area.dispatchEvent(new Event('input'));
    };
    area.addEventListener('focus', () => { area.__touched = true; });
    return h('div.col', { style: { gap: '4px' } },
      h('div.row.icon-bar', ICONS.map(([ch, name]) => h('button.btn.sm.ghost', { type: 'button', title: `Insert ${ch} (${name}) at the cursor`, onmousedown: (e) => e.preventDefault(), onclick: () => insert(ch) }, ch))),
      area);
  };
  const relPath = (abs) => {
    const root = (proj.rootFolder || '').replace(/[\\/]+$/, '');
    if (root && abs.toLowerCase().startsWith(root.toLowerCase() + '\\')) return abs.slice(root.length + 1);
    return abs;
  };
  const imagePicker = (obj, key, cls = '') => {
    const prev = h('div');
    let ticket = 0;
    const load = async () => {
      const mine = ++ticket; // only the latest preview is shown
      clear(prev);
      removeBtn.classList.toggle('hidden', !obj[key]);
      if (!obj[key]) return;
      let el;
      try {
        const url = await rpc('file.dataUrl', { path: obj[key], root: proj.rootFolder });
        el = url ? h('img.img-preview' + cls, { src: url }) : h('div.faint', 'Image not found (relative to the FOMOD root).');
      } catch (e) { el = h('div.faint', e.message); }
      if (mine === ticket) clear(prev).append(el);
    };
    const removeBtn = h('button.btn.sm.ghost', { title: 'Remove this image from the installer (the file itself stays)', onclick: () => { i.value = ''; obj[key] = ''; changed(); load(); } }, 'Remove');
    const i = h('input', { type: 'text', value: obj[key] || '', placeholder: 'Images\\Header.png' });
    i.addEventListener('change', () => { obj[key] = i.value.trim(); changed(); load(); });
    load();
    return h('div.col', h('div.path-input', i, h('button.btn.sm', { onclick: async () => {
      const f = await rpc('pick.file', { title: 'Choose an image', filter: 'Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp' });
      if (f) { i.value = relPath(f); obj[key] = i.value; changed(); load(); }
    } }, 'Browse'), removeBtn), prev);
  };

  let shownKey = null;
  function renderEditor() {
    const same = shownKey === selKey; shownKey = selKey;
    keepScroll(edit, () => renderEditorNow(), same);
  }
  function renderEditorNow() {
    clear(edit);
    const f = find(selKey);
    if (f.kind === 'project') edit.append(projectPanel());
    else if (f.kind === 'json') edit.append(jsonPanel());
    else if (f.kind === 'step') edit.append(stepPanel(f.step));
    else if (f.kind === 'group') edit.append(groupPanel(f.step, f.group));
    else edit.append(optionPanel(f.step, f.group, f.option));
  }

  function projectPanel() {
    const i = proj.info;
    const groups = h('input', { type: 'text', value: (i.groups || []).join(', '), style: { width: '100%' } });
    groups.addEventListener('input', () => { i.groups = groups.value.split(',').map((x) => x.trim()).filter(Boolean); changed(); refreshSummary(); });
    const pathIn = (key, title) => {
      const el = h('input', { type: 'text', value: proj[key] || '' });
      el.addEventListener('change', () => { proj[key] = el.value.trim(); changed(); refreshSummary(); });
      return h('div.path-input', el, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) { el.value = f; proj[key] = f; changed(); refreshSummary(); } } }, 'Browse'));
    };
    const main = h('div.fomod-main',
      h('div.banner.info', { style: { margin: '0 0 12px' } }, h('div',
        h('b', 'Your edits are saved in DBVO Studio, not in your FOMOD folder. '),
        'The ModuleConfig.xml there stays as it was. To get the installer with your changes, use ', h('b', 'Build ▾'), ': it writes the finished installer as a folder to ',
        h('span.mono', proj.releaseFolder || '<release folder>'), ' (named like the archive), and Build and pack also zips it for you. Zipping your FOMOD folder by hand would ship the old installer.')),
      sec('Installer', null, h('div.scards',
        card('Project name', 'Only shown in this app.', txt(proj, 'name', { after: renderHead })),
        card('Title in the installer', 'moduleName in ModuleConfig.xml: the big title MO2 and Vortex show above every page.', txt(proj, 'moduleName')),
        card('FOMOD root folder', 'The folder that holds your patch folders and images. Relative file sources start here.', pathIn('rootFolder', 'FOMOD root folder')),
        card('Release folder', 'The archive is written here, named from the mod name and version; so does the unpacked installer, as a folder with the same name.', pathIn('releaseFolder', 'Release folder')),
        h('div.scards3.wide',
          card('Archive', '.zip needs nothing; .7z uses 7-Zip (Global Settings) and is usually smaller.', segmented([['zip', '.zip'], ['7z', '.7z']], proj.archiveFormat, (x) => { proj.archiveFormat = x; changed(); renderEditor(); renderHead(); })),
          card('Voice files', 'Loose files: thousands of .fuz files, as before. BSA: each option installs one archive plus the plugin that loads it (its DBVO Fix plugin, or a small empty light plugin), which installs faster and keeps the Data folder tidy. The option must be active in the load order for the game to read the BSA.',
            h('div.row', { style: { flexWrap: 'nowrap' } }, segmented([['loose', 'Loose'], ['bsa', 'BSA']], proj.packBsa ? 'bsa' : 'loose', (x) => { proj.packBsa = x === 'bsa'; changed(); renderEditor(); }), helpButton('pack-as-bsa'))),
          card('XML encoding', 'UTF-16 is what FOMOD Creation Tool writes; both work in MO2 and Vortex.', segmented([['utf-16', 'UTF-16'], ['utf-8', 'UTF-8']], proj.encoding, (x) => { proj.encoding = x; changed(); renderEditor(); }))),
        card('Header image', 'moduleImage: shown at the top of every installer page. Relative to the FOMOD root; about 600 × 120 px or wider looks best.', imagePicker(proj, 'moduleImage', '.hdr'), { wide: true }))),
      sec('info.xml', null, h('div.scards',
        card('Mod name', 'The name MO2 and Vortex show for the mod.', txt(i, 'name')),
        card('Author', 'You, or your team.', txt(i, 'author')),
        card('Version', 'Also used in the archive name.', txt(i, 'version')),
        card('Website', 'e.g. your Nexus page.', txt(i, 'website')),
        card('Groups', 'Categories, comma separated, e.g. Audio - Voice.', groups, { wide: true }),
        card('Description', 'Shown by the mod manager in the mod\'s info. The icon buttons insert at the cursor.', descArea(i, 'description', 8), { wide: true }))),
      proj.moduleDependenciesXml || proj.conditionalFileInstallsXml || (proj.requiredInstallFiles || []).length
        ? sec('Kept from the imported FOMOD', null,
          h('div.help', { style: { margin: '10px 0' } }, 'These parts have no simple editor. They are written back exactly as they are; edit the XML here if you know the format.'),
          proj.moduleDependenciesXml != null ? srow('Module dependencies', null, txt(proj, 'moduleDependenciesXml', { area: true, rows: 5 })) : null,
          proj.conditionalFileInstallsXml != null ? srow('Conditional file installs', null, txt(proj, 'conditionalFileInstallsXml', { area: true, rows: 8 })) : null,
          (proj.requiredInstallFiles || []).length ? srow('Required install files', null, filesTable(proj.requiredInstallFiles)) : null)
        : null);
    summaryEl = h('div.fomod-summary');
    paintSummary();
    refreshSummary();
    return h('div.fomod-proj', main, summaryEl);
  }

  // ---- build summary (right of the Installer settings)
  let summaryEl = null, summary = null;
  const refreshSummary = debounce(async () => {
    try { summary = await rpc('fomod.summary', { project: proj }); } catch { summary = null; }
    paintSummary();
  }, 400);
  const fmtBytes = (b) => b >= 1e9 ? (b / 1e9).toFixed(2) + ' GB' : b >= 1e6 ? (b / 1e6).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1e3)) + ' KB';
  function paintSummary() {
    if (!summaryEl) return;
    const sm = summary;
    const val = store.get('fomodVal:' + id, null);
    const row = (k, v, tip) => [h('div', { title: tip || '' }, k), h('div.mono', { title: tip || '' }, v)];
    clear(summaryEl).append(
      h('h3', 'Build summary'),
      sm ? h('div.kv.sum-kv',
        row('Pages', String(sm.pages)), row('Groups', String(sm.groups)), row('Options', String(sm.options)),
        row('DBVO Patches', sm.patches ? `${sm.patches} (${sm.linked} options)` : '0', 'Options linked to a DBVO Patch; ⟳ Refresh linked updates their files'),
        row('Voice files', sm.voiceFiles.toLocaleString(), '.fuz files the options install (each counted once)'),
        sm.archives ? row('BSA archives', String(sm.archives)) : null,
        row('Files in all', sm.files.toLocaleString()),
        row('Estimated size', sm.bytes ? '≈ ' + fmtBytes(sm.bytes) : '—', 'The files before packing. Voice files are already compressed, so the archive is about the same size.'),
        sm.missing ? [h('div', { style: { color: 'var(--bad)' } }, 'Missing sources'), h('div.mono', { style: { color: 'var(--bad)' }, title: 'File or folder sources that are not on disk. Validate lists them.' }, String(sm.missing))] : null)
        : h('div.faint', 'Counting…'),
      h('div.sum-sep'),
      h('div.row', { style: { gap: '8px' } }, h('b', 'Last check'), h('div.spacer'), h('button.btn.sm', { title: 'Check against the ModuleConfig 5.0 schema and for missing files', onclick: validate }, 'Validate')),
      val ? h('div', { style: { color: val.errors ? 'var(--bad)' : val.warnings ? 'var(--warn)' : 'var(--ok)', marginTop: '4px' }, title: 'Run Validate again after changes' },
        val.errors || val.warnings ? `${val.errors} error(s), ${val.warnings} warning(s)` : '✓ No problems found', h('div.faint', { style: { fontSize: '12px' } }, new Date(val.at).toLocaleString()))
        : h('div.faint', { style: { marginTop: '4px' } }, 'Not checked yet.'),
      h('div.sum-sep'),
      h('div', h('b', 'Last build')),
      proj.lastBuildUtc ? h('div', { style: { marginTop: '4px' } }, new Date(proj.lastBuildUtc).toLocaleString(), proj.lastArchive ? h('div.mono.faint.ellipsis', { title: proj.lastArchive, style: { fontSize: '12px' } }, proj.lastArchive.split(/[\\/]/).pop()) : null,
        proj.lastArchive ? h('button.btn.sm.ghost', { style: { marginTop: '4px' }, title: 'Show the archive in Explorer', onclick: () => rpc('open.path', { path: proj.lastArchive.replace(/[\\/][^\\/]*$/, '') }).catch(fail) }, 'Open folder') : null)
        : h('div.faint', { style: { marginTop: '4px' } }, 'Not built yet.'));
  }

  function jsonPanel() {
    const j = proj.json;
    const carriers = proj.steps.flatMap((s) => s.groups.flatMap((g) => g.options.filter((o) => o.carriesJson).map((o) => ({ s, g, o }))));
    const preview = h('div.box.mono', { style: { whiteSpace: 'pre' } });
    const upd = () => {
      const o = { voice_pack_name: j.voicePackName, voice_pack_id: j.voicePackId };
      preview.textContent = JSON.stringify(o, null, 2);
    };
    upd();
    return h('div', sec('DBVO voice pack JSON', h('div.row', helpButton('4-the-dbvo-json'), switchEl(j.enabled, (x) => { j.enabled = x; changed(true); renderEditor(); }, 'Add to the installer')),
      (dto.existingJson || []).length ? h('div.banner.info', { style: { margin: '10px 0 0' } }, h('div',
        h('b', 'Your installer already installs this file, so DBVO Studio adds none of its own. '),
        'Found: ', (dto.existingJson || []).join(' · '), '. The settings below are only used if that file goes away; you can turn off "Add to the installer".')) : null,
      h('div.help', { style: { margin: '10px 0' } }, 'This small file makes the voice selectable in DBVO\'s MCM. It is written when you build and installed by the option marked "Carries the DBVO JSON" (normally a Required core option).',
        h('p', 'Location in the game\'s Data folder: ', h('span.mono', dto.jsonPath), ' (', dto.naming === 'Legacy' ? 'Legacy / DBVO 1' : dto.naming === 'Dbrev' ? 'DBReV' : 'DBVO 2.0', ', from the voice pack\'s pack format).')),
      srow('voice_pack_name', dto.naming === 'Dbrev' ? 'Shown in Dragonborn ReVoiced\'s menu.' : 'Shown in DBVO\'s menu.', txt(j, 'voicePackName', { after: upd })),
      srow('voice_pack_id', dto.naming === 'Dbrev' ? 'Must match the folder under Sound\\DBReV\\.' : 'Must match the folder under Sound\\DBVO\\.', txt(j, 'voicePackId', { after: upd })),
      srow('Carried by', null, carriers.length ? h('div.col', carriers.map(({ s, g, o }) => h('a', { href: '#', onclick: (e) => { e.preventDefault(); select(o.id); } }, `${s.name} › ${g.name} › ${o.name}`)))
        : h('span.badge.b-broken', 'No option carries it — tick "Carries the DBVO JSON" on a Required option')),
      srow('Preview', null, preview)));
  }

  function stepPanel(st) {
    return h('div', sec('Page (install step)', h('div.row', h('div.spacer'),
      ...moveButtons(proj.steps, st, 'page'),
      h('button.btn.sm', { title: 'Add a new page right above this one', onclick: () => addStep(proj.steps.indexOf(st)) }, '+ Page above'),
      h('button.btn.sm', { title: 'Add a new page right below this one', onclick: () => addStep(proj.steps.indexOf(st) + 1) }, '+ Page below'),
      h('button.btn.sm.danger', { title: 'Delete this page and its groups (asks first)', onclick: () => deleteStep(st) }, 'Delete')),
      h('div.faint', { style: { padding: '8px 0 0' } }, `Page ${proj.steps.indexOf(st) + 1} of ${proj.steps.length}`),
      srow('Name', null, txt(st, 'name', { tree: true })),
      srow('Order of the pages', 'All pages. As listed keeps the order in the tree; A–Z and Z–A sort them by name in the installer, whatever their order here.', segmented([['Explicit', 'As listed'], ['Ascending', 'A–Z'], ['Descending', 'Z–A']], proj.stepsOrder || 'Explicit', (x) => { proj.stepsOrder = x; changed(); renderEditor(); })),
      srow('Order of the groups', 'On this page.', segmented([['Explicit', 'As listed'], ['Ascending', 'A–Z'], ['Descending', 'Z–A']], st.groupsOrder || 'Explicit', (x) => { st.groupsOrder = x; changed(); renderEditor(); })),
      srow('Visibility', 'Optional conditions (flags / plugins) for showing this page. Kept from imports; edit as XML.', st.visibleXml != null ? txt(st, 'visibleXml', { area: true, rows: 4 })
        : h('button.btn.sm', { title: 'Show this page only when a condition is met (starts with an example you can edit)', onclick: () => { st.visibleXml = '<visible operator="And">\n  <fileDependency file="Example.esp" state="Active" />\n</visible>'; changed(); renderEditor(); } }, '+ Add a condition')),
      srow('Groups', null, h('div.col', st.groups.map((g) => h('a', { href: '#', onclick: (e) => { e.preventDefault(); select(g.id); } }, `${g.name} · ${g.options.length} option(s)`)),
        h('button.btn.sm', { title: 'Add a group of options to this page', style: { alignSelf: 'flex-start' }, onclick: () => addGroup(st) }, '+ Group')))));
  }

  function groupPanel(st, g) {
    const typeSel = h('select', GROUP_TYPES.map(([v, t]) => h('option', { value: v, selected: v === g.type }, t)));
    const typeHelp = h('div.faint', (GROUP_TYPES.find((t) => t[0] === g.type) || [])[2] || '');
    typeSel.addEventListener('change', () => { g.type = typeSel.value; typeHelp.textContent = (GROUP_TYPES.find((t) => t[0] === g.type) || [])[2] || ''; changed(true); });
    return h('div', sec('Group', h('div.row', h('div.spacer'),
      ...moveButtons(st.groups, g, 'group'),
      h('button.btn.sm', { title: 'Add a new group right below this one, on the same page', onclick: () => addGroup(st, st.groups.indexOf(g) + 1) }, '+ Group below'),
      h('button.btn.sm.danger', { title: 'Delete this group and its options (asks first)', onclick: () => deleteGroup(st, g) }, 'Delete')),
      h('div.faint', { style: { padding: '8px 0 0' } }, st.name),
      srow('Name', null, txt(g, 'name', { tree: true })),
      srow('Selection', null, h('div.col', typeSel, typeHelp)),
      srow('Order', 'Explicit keeps your order; Ascending sorts A–Z in the installer.', segmented([['Explicit', 'As listed'], ['Ascending', 'A–Z'], ['Descending', 'Z–A']], g.order, (x) => { g.order = x; changed(); renderEditor(); })),
      srow('Options', null, h('div.col',
        h('div.row', h('button.btn.sm', { title: 'Add an empty option to this group', onclick: () => { const o = blankOption(); g.options.push(o); changed(); select(o.id); } }, '+ Option'),
          h('button.btn.sm', { title: 'Put Required options first, then sort the rest by name', onclick: () => { g.options.sort((a, b) => (a.mode === 'Required' ? -1 : 0) - (b.mode === 'Required' ? -1 : 0) || (a.name || '').localeCompare(b.name || '', undefined, { numeric: true, sensitivity: 'base' })); restructure(); } }, 'Sort A–Z')),
        g.options.map((o) => h('a', { href: '#', onclick: (e) => { e.preventDefault(); select(o.id); } }, o.name || '(option)'))))));
  }

  const blankOption = () => ({ id: newId(), name: 'New option', description: '', image: '', files: [], mode: 'Optional', plugins: [], requireAll: false, notUsableWhenMissing: true, typeDescriptorXml: null, conditionFlagsXml: null, flagsFirst: false, patchId: null, modId: null, carriesJson: false });

  function filesTable(list, onRemove) {
    const tbody = h('tbody');
    const render = () => {
      clear(tbody);
      list.forEach((f, i) => {
        const kind = h('select', h('option', { value: 'folder', selected: f.kind === 'folder' }, 'folder'), h('option', { value: 'file', selected: f.kind === 'file' }, 'file'));
        kind.addEventListener('change', () => { f.kind = kind.value; changed(); });
        const src = h('input', { type: 'text', value: f.source, placeholder: 'relative to the FOMOD root' });
        src.addEventListener('input', () => { f.source = src.value; changed(); });
        const dst = h('input', { type: 'text', value: f.destination, placeholder: f.kind === 'folder' ? 'e.g. Sound' : 'e.g. DBVO Fix - Mod.esp' });
        dst.addEventListener('input', () => { f.destination = dst.value; changed(); });
        // A linked file comes from the DBVO Patch and is rewritten from it by Refresh linked and Build: its kind, source
        // and destination can't be edited here (remove it with ×, then add your own); its priority can.
        if (f.linked) {
          const why = 'From the DBVO Patch: Refresh linked and Build keep it up to date. To use something else, remove it (×) and add your own.';
          for (const el of [kind, src, dst]) { el.disabled = true; el.title = why; }
        }
        const pr = h('input', { type: 'number', value: f.priority || 0, style: { width: '60px' } });
        pr.addEventListener('change', () => { f.priority = Number(pr.value) || 0; changed(); });
        tbody.append(h('tr', h('td', { style: { width: '90px' } }, kind),
          h('td', h('div.path-input', src, h('button.btn.sm', { disabled: !!f.linked, onclick: async () => { const p = f.kind === 'folder' ? await rpc('pick.folder', { title: 'Source folder' }) : await rpc('pick.file', { title: 'Source file' }); if (p) { f.source = relPath(p); src.value = f.source; changed(); } } }, '…'))),
          h('td', dst), h('td', pr), h('td', h('button.icon-btn', { title: f.linked ? 'Remove (stays removed: Refresh linked and Build won\'t add it back)' : 'Remove', onclick: () => { list.splice(i, 1); if (onRemove) onRemove(f); render(); changed(); } }, '×'))));
      });
    };
    render();
    return h('div', h('table.tbl.ftbl', h('thead', h('tr', h('th', 'Kind'), h('th', 'Source'), h('th', 'Installs to (Data\\…)'), h('th', 'Priority'), h('th', ''))), tbody),
      h('div.row', { style: { marginTop: '6px' } },
        h('button.btn.sm', { onclick: async () => { const p = await rpc('pick.folder', { title: 'Folder to install' }); if (!p) return; const name = p.split('\\').pop(); list.push({ kind: 'folder', source: relPath(p), destination: /^(sound|meshes|textures|interface|scripts|skse|dragonbornvoiceover)$/i.test(name) ? name : '', priority: 0 }); render(); changed(); } }, '+ Folder'),
        h('button.btn.sm', { onclick: async () => { const p = await rpc('pick.file', { title: 'File to install' }); if (!p) return; list.push({ kind: 'file', source: relPath(p), destination: p.split('\\').pop(), priority: 0 }); render(); changed(); } }, '+ File'),
        h('span.faint', 'Destination is relative to the game\'s Data folder; empty = the Data folder itself.')));
  }

  function optionPanel(st, g, o) {
    const modeSel = h('select', MODES.filter(([v]) => v !== 'Advanced' || o.mode === 'Advanced' || true).map(([v, t]) => h('option', { value: v, selected: v === o.mode }, t)));
    const modeHelp = h('div.faint', (MODES.find((m) => m[0] === o.mode) || [])[2] || '');
    modeSel.addEventListener('change', () => {
      o.mode = modeSel.value;
      if (o.mode === 'Advanced' && !o.typeDescriptorXml) o.typeDescriptorXml = '<typeDescriptor>\n  <type name="Optional" />\n</typeDescriptor>';
      changed(true);
      renderEditor();
    });
    const pluginsBox = h('div.row.wrap');
    const renderPlugins = () => {
      const add = h('input', { type: 'text', placeholder: 'Plugin.esp, then Enter', style: { width: '200px' } });
      add.addEventListener('keydown', (e) => { if (e.key === 'Enter' && add.value.trim()) { o.plugins.push(add.value.trim()); changed(true); renderPlugins(); } });
      clear(pluginsBox).append(o.plugins.map((p, i) => h('span.chip', h('span.mono', p), h('button.icon-btn', { title: 'Remove this plugin from the option', onclick: () => { o.plugins.splice(i, 1); changed(true); renderPlugins(); } }, '×'))), add);
    };
    renderPlugins();
    return h('div',
      sec('Option', h('div.row', h('div.spacer'),
        ...moveButtons(g.options, o, 'option'),
        h('button.btn.sm', { title: 'Make a copy of this option in the same group (not linked to the DBVO Patch, so Refresh linked leaves it alone)', onclick: () => { const c = structuredClone(o); c.id = newId(); c.name += ' (copy)'; c.carriesJson = false; c.patchId = null; c.modId = null; g.options.splice(g.options.indexOf(o) + 1, 0, c); changed(); select(c.id); } }, 'Duplicate'),
        h('button.btn.sm.danger', { title: 'Delete this option (asks first)', onclick: async () => {
          if (!await confirmBox('Delete option?', `Delete "${o.name}"?` + (o.carriesJson ? ' It carries the DBVO JSON: tick "Carries the DBVO JSON" on another Required option afterwards.' : ''), 'Delete', true)) return;
          const i = g.options.indexOf(o); if (i >= 0) { g.options.splice(i, 1); changed(); select(g.id); }
        } }, 'Delete')),
        h('div.faint', { style: { padding: '8px 0 0' } }, `${st.name} › ${g.name}`),
        srow('Name', null, txt(o, 'name', { tree: true })),
        srow('Description', 'Shown when the user hovers the option. Links can be copied from here by your users.', descArea(o, 'description', 7)),
        srow('Image', 'Shown next to the description. Relative to the FOMOD root.', imagePicker(o, 'image')),
        o.patchId ? srow('Linked to', 'Files below come from this DBVO Patch; "⟳ Refresh linked" updates them.', h('div.row', h('span.badge.b-fixed', '🔗 ' + ((app.state.patches.find((p) => p.id === o.patchId) || {}).name || 'a DBVO Patch')),
          h('button.btn.sm.ghost', { title: 'Stop updating this option\'s files from the DBVO Patch', onclick: () => { o.patchId = null; o.modId = null; changed(true); renderEditor(); } }, 'Unlink'))) : null,
        srow('Carries the DBVO JSON', 'The voice pack file is installed with this option. Use a Required one.', switchEl(o.carriesJson, (x) => {
          if (x) for (const s2 of proj.steps) for (const g2 of s2.groups) for (const o2 of g2.options) o2.carriesJson = false;
          o.carriesJson = x; changed(true); renderEditor();
        }))),
      sec('Offered as', helpButton('3-edit-pages-groups-and-options'),
        srow('Type', null, h('div.col', modeSel, modeHelp)),
        o.mode === 'Detect' ? srow('Detect these plugins', 'Plugin file names as in the load order.', h('div.col', pluginsBox,
          h('div.row', segmented([['any', 'Any of them'], ['all', 'All of them']], o.requireAll ? 'all' : 'any', (x) => { o.requireAll = x === 'all'; changed(); renderEditor(); }),
            switchEl(o.notUsableWhenMissing, (x) => { o.notUsableWhenMissing = x; changed(); }, 'Grey out (Not usable) when missing')))) : null,
        o.mode === 'Advanced' ? srow('Type descriptor XML', 'Written exactly as typed.', txt(o, 'typeDescriptorXml', { area: true, rows: 12 })) : null,
        o.conditionFlagsXml != null ? srow('Condition flags XML', 'Flags this option sets for later pages or conditional installs.', txt(o, 'conditionFlagsXml', { area: true, rows: 4 })) : null),
      sec('Files', null, filesTable(o.files, (f) => {
        // A linked file you remove stays removed: Refresh linked and Build leave it out from now on.
        if (f.linked && o.patchId) { o.removedLinked = [...new Set([...(o.removedLinked || []), f.destination])]; renderEditor(); }
      }),
      (o.removedLinked || []).length ? h('div.row.wrap', { style: { marginTop: '8px' } }, h('span.faint', 'Removed from the linked files (not added back by Refresh linked or Build):'),
        o.removedLinked.map((d) => h('span.chip', h('span.mono', d), h('button.icon-btn', { title: 'Put it back: the next Refresh linked or Build adds it again (when the file exists)', onclick: () => { o.removedLinked = o.removedLinked.filter((x) => x !== d); changed(); renderEditor(); } }, '↺')))) : null));
  }

  // ---- actions
  // While the backend works on the whole project (and answers with its new copy), the editor takes no input at all,
  // keyboard included: an edit made meanwhile would be overwritten by that answer.
  async function editorWaits(promise, title) {
    if (document.activeElement && view.contains(document.activeElement)) document.activeElement.blur();
    view.inert = true;
    try { return await withBusy(promise, title, ''); } finally { view.inert = false; }
  }
  async function addPatches() {
    await refreshState();
    // Every patch of this voice pack, Voice Changer on or off: both build .fuz files into the final folder.
    const patches = app.state.patches.filter((p) => !proj.voicePackId || p.voicePackId === proj.voicePackId);
    if (!patches.length) { toast('No DBVO Patches for this voice pack yet.', true); return; }
    // A patch makes one option per mod, named after the mod: it is "already in" when every one of its mods has its
    // linked option, and "same name in" when an option of one of its mods' names is there but not linked to it.
    const allOpts = proj.steps.flatMap((s) => s.groups.flatMap((g) => g.options));
    const pairs = new Set(allOpts.filter((o) => o.patchId).map((o) => o.patchId + '|' + o.modId));
    const optionNames = new Set(allOpts.map((o) => (o.name || '').trim().toLowerCase()));
    const modName = (mid) => ((app.state.mods || []).find((m) => m.id === mid) || {}).name || '';
    const modsOf = (p) => (p.modIds || []);
    const isIn = (p) => modsOf(p).length > 0 && modsOf(p).every((mid) => pairs.has(p.id + '|' + mid));
    const partlyIn = (p) => !isIn(p) && modsOf(p).some((mid) => pairs.has(p.id + '|' + mid));
    const sameName = (p) => modsOf(p).some((mid) => !pairs.has(p.id + '|' + mid) && optionNames.has(modName(mid).trim().toLowerCase()));
    // Nothing is ticked to start with: tick the patches to add (in an imported FOMOD most mods are already there under
    // their own options, which aren't linked to a patch).
    const boxes = patches.map((p) => ({ p, box: h('input', { type: 'checkbox', checked: false }) }));
    const addBtnLabel = () => { const n = boxes.filter((x) => x.box.checked).length; const b = document.querySelector('#modal .modal-card .btn.primary'); if (b) b.textContent = n ? `Add ${n}` : 'Add'; };
    boxes.forEach(({ box }) => box.addEventListener('change', addBtnLabel));
    const listEl = h('div.box', { style: { maxHeight: '320px', overflow: 'auto' } });
    let q = '';
    const shown = () => boxes.filter(({ p }) => !q || p.name.toLowerCase().includes(q) || (p.category || '').toLowerCase().includes(q));
    const paintList = () => clear(listEl).append(...shown().map(({ p, box }) => h('label.row', box, h('span.grow', p.name), h('span.faint', p.category),
      isIn(p) ? h('span.badge.b-skip', 'already in') : sameName(p) ? h('span.badge.b-skip', { title: 'An option named like one of its mods is already in the FOMOD (not linked to this patch): adding would make a second one' }, 'same name in')
        : partlyIn(p) ? h('span.badge.b-skip', { title: 'Some of its mods are in the FOMOD already; adding puts in the others' }, 'partly in') : null)));
    paintList();
    const search = h('input', { type: 'text', placeholder: 'Find a patch or category…', style: { flex: '1' } });
    search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); paintList(); });
    const tickTools = h('div.row', search,
      h('button.btn.sm', { title: 'Tick every patch shown that isn\'t in this FOMOD yet (one patch per mod: a second patch of the same mod is left for you to pick)', onclick: () => {
        const claimed = new Set(allOpts.filter((o) => o.modId).map((o) => o.modId));
        shown().forEach(({ p, box }) => {
          box.checked = !isIn(p) && !sameName(p) && !(modsOf(p).length && modsOf(p).every((mid) => claimed.has(mid)));
          if (box.checked) modsOf(p).forEach((mid) => claimed.add(mid));
        });
        addBtnLabel();
      } }, 'Tick new ones'),
      h('button.btn.sm', { title: 'Untick every patch', onclick: () => { boxes.forEach(({ box }) => { box.checked = false; }); addBtnLabel(); } }, 'Untick all'));
    let sort = true;
    // With an option picked in the tree, the new ones can go right above or below it (in its group) instead of on their category's page.
    const anchor = (() => { const f = find(selKey); return f && f.kind === 'option' ? f : null; })();
    let place = anchor ? 'below' : 'category';
    const sortRow = h('div', switchEl(sort, (x) => { sort = x; }, 'Sort each group A–Z afterwards'));
    const placeRow = anchor ? h('div.col', { style: { gap: '6px' } }, h('div.field-label', 'Where'),
      segmented([['above', `Above “${anchor.option.name}”`], ['below', `Below “${anchor.option.name}”`], ['category', 'On their category\'s page']], place, (v) => { place = v; sortRow.classList.toggle('hidden', v !== 'category'); }),
      h('div.faint', `Above or below: in the group “${anchor.group.name}”, in the order listed here; no category page is added.`)) : null;
    if (anchor) sortRow.classList.add('hidden');
    const ok = await modal('Add DBVO Patches', [
      h('div', 'Each mod becomes an option on the page of its category (made if missing), detecting the mod\'s plugins and installing its Sound folder and DBVO Fix plugins from the patch\'s final folder. Patches already in this FOMOD only get their files updated.'),
      tickTools,
      listEl,
      placeRow,
      sortRow,
    ], [{ label: 'Cancel', value: false }, { label: 'Add', primary: true, value: true }], { help: '2-add-your-patches' });
    if (!ok) return;
    if (!boxes.some((x) => x.box.checked)) { toast('Nothing ticked: no options added.'); return; }
    try {
      // The editor waits while the backend adds them (an edit made meanwhile would be overwritten by its answer).
      const r = await editorWaits((async () => {
        await settleSaves();
        return rpc('fomod.addPatches', { project: proj, patchIds: boxes.filter((x) => x.box.checked).map((x) => x.p.id), sort: sort && place === 'category' ? 'az' : '',
          ...(anchor && place !== 'category' ? { anchorId: anchor.option.id, where: place } : {}) });
      })(), 'Adding DBVO Patches');
      dto = r.project; proj = dto.project;
      toast(`${r.added} option(s) added, ${r.updated} updated.`);
      renderAll();
    } catch (e) { failOnce(e); }
  }

  async function refreshLinked() {
    try {
      const r = await editorWaits((async () => { await settleSaves(); return rpc('fomod.refresh', { project: proj }); })(), 'Updating linked options');
      dto = r.project; proj = dto.project; renderAll();
      toast(r.orphans && r.orphans.length ? `Linked options updated. Not linked any more (left as they are): ${r.orphans.join(', ')}` : 'Linked options updated.');
    } catch (e) { failOnce(e); }
  }

  async function validate() {
    try {
      const issues = await rpc('fomod.validate', { project: proj });
      const errors = issues.filter((i) => i.level === 'error');
      store.set('fomodVal:' + id, { errors: errors.length, warnings: issues.length - errors.length, at: Date.now() });
      paintSummary();
      await modal(issues.length ? `${errors.length} error(s), ${issues.length - errors.length} warning(s)` : 'No problems found', [
        issues.length ? h('div.box', { style: { maxHeight: '60vh', overflow: 'auto', padding: 0 } }, issues.map((i) => h('div.issue', { onclick: () => { if (i.optionId) { closeAllModals(); select(i.optionId); } } },
          h('span.badge.' + (i.level === 'error' ? 'b-broken' : 'b-toolong'), i.level), h('span.faint', i.where), h('span', i.message))))
          : h('div', 'The XML passes the ModConfig 5.0 schema, every file is in place and the DBVO JSON is carried by an option.'),
      ], undefined, { wide: true, help: '5-check-it' });
    } catch (e) { fail(e); }
  }

  async function simulate() {
    const linkedPlugins = new Set(['Skyrim.esm', 'Update.esm', 'Dawnguard.esm', 'HearthFires.esm', 'Dragonborn.esm']);
    for (const s of proj.steps) for (const g of s.groups) for (const o of g.options) if (o.mode === 'Detect') o.plugins.forEach((p) => linkedPlugins.add(p));
    const plugins = h('textarea', { rows: 8, placeholder: 'One plugin per line' }, store.get('simPlugins:' + id, [...linkedPlugins].join('\n')));
    const out = h('div');
    const choices = {};
    const run = async () => {
      store.set('simPlugins:' + id, plugins.value);
      try {
        const r = await rpc('fomod.simulate', { project: proj, plugins: plugins.value.split(/\r?\n/), choices });
        clear(out).append(
          h('div.field-label', 'Pages'),
          h('div.box', { style: { maxHeight: '36vh', overflow: 'auto' } }, r.steps.map((s) => h('div', { style: { marginBottom: '8px' } },
            h('div', h('b', s.name), s.visible ? null : h('span.faint', ' · hidden by its conditions')),
            s.groups.map((g) => h('div', { style: { paddingLeft: '14px' } }, h('div.faint', `${g.name} · ${g.type}`),
              g.options.map((o) => {
                const cb = h('input', { type: 'checkbox', checked: o.selected, disabled: o.locked });
                cb.addEventListener('change', () => { choices[o.id] = cb.checked; run(); });
                return h('label.row', { style: { paddingLeft: '14px' } }, cb, h('span', o.name), h('span.badge.' + ({ Required: 'b-fixed', Recommended: 'b-valid', NotUsable: 'b-broken' }[o.type] || 'b-skip'), o.type));
              })))))),
          h('div.field-label', { style: { marginTop: '10px' } }, `Would install (${r.files.length} entries)`),
          h('div.box.mono', { style: { maxHeight: '22vh', overflow: 'auto', fontSize: '12px' } }, r.files.map((f) => h('div', { style: { color: f.exists ? '' : 'var(--bad)' } }, `${f.exists ? '✓' : '✕ missing'}  ${f.source} → Data\\${f.destination}${f.kind === 'folder' || f.fileCount > 1 ? `  (${f.fileCount} files)` : ''}  · ${f.option}`))),
          r.conflicts.length ? h('div.banner.warn', { style: { margin: '10px 0 0' } }, h('div', h('b', `${r.conflicts.length} file(s) installed by more than one option: `), h('div.mono', { style: { fontSize: '11.5px', maxHeight: '120px', overflow: 'auto' } }, r.conflicts.slice(0, 50).join('\n')))) : null,
          r.flags.length ? h('div.faint', { style: { marginTop: '6px' } }, r.flags.join(' · ')) : null);
      } catch (e) { fail(e); }
    };
    run();
    await modal('Simulate install', [
      h('div', 'Shows what Mod Organizer 2 or Vortex would pre-tick for a load order, and which files would install. Tick or untick options to try choices.'),
      h('div.row', { style: { alignItems: 'flex-start' } },
        h('div.col', { style: { width: '320px', flexShrink: 0 } }, h('div.field-label', 'Active plugins'), plugins,
          h('div.row', h('button.btn.sm', { onclick: async () => {
            const f = await rpc('pick.file', { title: 'plugins.txt (MO2 profile or %LOCALAPPDATA%\\Skyrim Special Edition)', filter: 'Load order|plugins.txt;loadorder.txt;*.txt' });
            if (!f) return;
            try { plugins.value = (await rpc('fomod.loadOrder', { path: f })).join('\n'); run(); } catch (e) { fail(e); }
          } }, 'Load plugins.txt…'), h('button.btn.sm.primary', { title: 'Simulate the install with these plugins active', onclick: run }, 'Run'))),
        h('div.grow', out)),
    ], undefined, { wide: true, help: '5-check-it' });
  }

  async function previewXml() {
    try {
      const r = await rpc('fomod.preview', { project: proj });
      const tabs = { 'ModuleConfig.xml': r.moduleConfig, 'info.xml': r.info, 'DBVO JSON': r.json };
      const box = h('textarea', { rows: 28, readonly: true, style: { fontFamily: 'Consolas, monospace', fontSize: '12px', whiteSpace: 'pre' } }, r.moduleConfig);
      let cur = 'ModuleConfig.xml';
      const seg = h('div');
      const renderSeg = () => clear(seg).append(segmented(Object.keys(tabs).map((k) => [k, k]), cur, (k) => { cur = k; box.value = tabs[k]; renderSeg(); }));
      renderSeg();
      await modal('Generated files', [seg, box, h('div.faint', 'Before packing, file sources outside the FOMOD root are mapped into _external\\ and the DBVO JSON into _common\\.')], undefined, { wide: true });
    } catch (e) { fail(e); }
  }

  async function runBuild(pack) {
    try {
      await settleSaves();
      await rpc('fomod.build', { project: proj, package: pack });
      renderHead();
    } catch (e) { failOnce(e); }
  }

  const onJobDone = (e) => {
    if (!document.body.contains(view)) { document.removeEventListener('job-done', onJobDone); return; }
    if (e.detail.kind !== 'fomod') return;
    const built = e.detail.project;
    if (built && built.id === id) {
      // The build refreshed the linked options and saved its copy: take its files and build info into this copy
      // (which may have edits made during the build) and save that, so neither is lost.
      proj.lastBuildUtc = built.lastBuildUtc; proj.lastArchive = built.lastArchive;
      const fresh = new Map();
      for (const st of built.steps || []) for (const g of st.groups || []) for (const o of g.options || []) fresh.set(o.id, o);
      // Only the linked files come from the build; files you added, removed or edited meanwhile stay as they are.
      const low = (x) => (x.destination || '').toLowerCase();
      let openChanged = false;
      const openOpt = find(selKey).option;
      for (const st of proj.steps) for (const g of st.groups) for (const o of g.options) {
        const f = fresh.get(o.id);
        if (!f || !o.patchId) continue;
        const removed = new Set((o.removedLinked || []).map((d) => d.toLowerCase()));
        // Options saved before files were marked linked: their Sound folder and DBVO Fix plugins are the patch's (as in Relink).
        const preFlag = !o.files.some((x) => x.linked) && !(o.removedLinked || []).length;
        const oldLinked = (x) => preFlag && ((x.kind === 'folder' && low(x).replace(/^[\\/]+|[\\/]+$/g, '') === 'sound') || (x.kind === 'file' && low(x).endsWith('.esp') && low(x).startsWith('dbvo fix - ')));
        const mine = o.files.filter((x) => !x.linked && !oldLinked(x));
        const was = new Map(o.files.filter((x) => x.linked).map((x) => [low(x), x]));
        const linked = f.files.filter((x) => x.linked && !removed.has(low(x)) && !mine.some((m) => low(m) === low(x) && (x.kind === 'file' || (m.source || '').toLowerCase() === (x.source || '').toLowerCase())))
          .map((x) => { const w = was.get(low(x)); return w ? { ...x, priority: w.priority } : x; }); // your priority stays
        const next = [...linked, ...mine];
        if (JSON.stringify(next) === JSON.stringify(o.files)) continue;
        o.files.splice(0, o.files.length, ...next); // the same list: an open file table keeps writing into it
        if (o === openOpt) openChanged = true;
      }
      changed();
      renderTree();
      if (openChanged) renderEditor();
    }
    renderHead();
    paintSummary();
  };
  if (offJobDone) offJobDone();
  document.addEventListener('job-done', onJobDone);
  offJobDone = () => { document.removeEventListener('job-done', onJobDone); window.removeEventListener('pagehide', flushOnLeave); window.removeEventListener('beforeunload', flushOnLeave); flushOnLeave(); };

  function renderAll() { renderHead(); renderTree(); renderEditor(); }
  renderAll();
  return view;
}
