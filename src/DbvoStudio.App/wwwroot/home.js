// The landing page: a welcome, what to do next, the state of the studio and the work to pick up again.
import { rpc, h, store, icon } from './lib.js';
import { app, go, refreshState } from './app.js';

const CHOICES = [
  { icon: 'wave', color: '#58a6ff', title: 'I want to make DBVO patches.', sub: 'Import a mod\'s plugins, review the player lines, then voice them.', go: () => go('mods') },
  { icon: 'mic', color: '#d2a8ff', title: 'I want to revoice an NPC or a whole mod.', sub: 'Give characters in any mod, or the base game, a new voice.', go: () => go('revoices') },
  { icon: 'box', color: '#3fb950', title: 'I want to build a FOMOD installer.', sub: 'Gather your patches into an installer with the DBVO JSON.', go: () => go('fomods') },
  { icon: 'lips', color: '#f0883e', title: 'I want lip sync for WAV files I already have.', sub: 'Whisper listens, FaceFXWrapper writes the .lip files.', go: () => go('wavlip') },
  { icon: 'gear', color: '#8b949e', title: 'I want to set up the studio.', sub: 'Library, tools, ElevenLabs key and voice packs.', go: () => go('settings', { tab: 'general' }) },
  { icon: 'book', color: '#e3b341', title: 'I want to read the guide.', sub: 'Every screen and button, explained.', go: () => go('help') },
];

const WAVE = [18, 30, 52, 40, 70, 88, 60, 94, 72, 46, 80, 100, 64, 38, 58, 84, 50, 28, 44, 22, 36, 16];
const RECENT_KEY = 'recent';
const RECENT_VIEWS = ['mod', 'patch', 'fomod', 'revoice'];

/** Remember a project page the user opened, newest first, for "Pick up where you left off". */
export function noteRecent(route) {
  if (!RECENT_VIEWS.includes(route.view) || !route.id) return;
  const list = store.get(RECENT_KEY, []).filter((r) => !(r.view === route.view && r.id === route.id));
  list.unshift({ view: route.view, id: route.id });
  store.set(RECENT_KEY, list.slice(0, 12));
}

export async function homeView() {
  const s = await refreshState();
  const revoices = await rpc('revoice.list').catch(() => []);
  const view = h('div.view.home');

  // ---- left: welcome, counts and the studio check
  const voiced = s.patches.reduce((a, p) => a + (p.generated || 0), 0);
  const stats = h('div.home-stats',
    [[s.mods.length, s.mods.length === 1 ? 'mod' : 'mods'], [s.patches.length, s.patches.length === 1 ? 'DBVO patch' : 'DBVO patches'], [voiced, 'lines voiced']]
      .map(([n, l]) => h('div', h('b', n.toLocaleString()), h('span', l))));
  const checks = h('div.home-checks');
  const chip = (ok, text, title, onclick) => h('button.home-chip' + (ok ? '.ok' : '.todo'), { title: title || '', onclick }, icon(ok ? 'check' : 'gear', { size: 13 }), text);
  const settings = () => go('settings', { tab: 'general' });
  const t = s.settings.tools;
  const root = s.libraryRoot || '';
  const shortPath = root.length <= 40;
  checks.append(chip(shortPath, `Library on ${root.slice(0, 3)} · ${shortPath ? 'short path' : 'long path'}`,
    shortPath ? root : `${root}\nA shorter path, near the top of a drive, keeps DBVO file paths under Windows' 260-character limit.`, settings));
  const tools = [['FonixData.cdf', t.fonix], ['xWMAEncode.exe', t.xwma], ['ffmpeg.exe', t.ffmpeg], ['FaceFXWrapper.exe', t.faceFx], ['7z.exe', t.sevenZip]];
  const missing = tools.filter(([, ok]) => !ok).map(([n]) => n);
  checks.append(chip(!missing.length, missing.length ? `${missing.length} of 5 tools missing` : 'All 5 tools found', missing.length ? 'Missing: ' + missing.join(', ') : '', settings));
  if (!s.voicePacks.length) checks.append(chip(false, 'No voice pack yet', 'Create one in Global Settings → Voice packs', () => go('settings', { tab: 'voices', id: 'new' })));
  const eleven = chip(s.settings.hasApiKey, s.settings.hasApiKey ? 'ElevenLabs key saved' : 'ElevenLabs key not set', '', settings);
  checks.append(eleven);
  if (s.settings.hasApiKey) {
    rpc('eleven.subscriptionInfo').then((c) => {
      const left = Math.max(0, c.characterLimit - c.characterCount);
      eleven.lastChild.textContent = `ElevenLabs · ${left.toLocaleString()} credits left`;
    }).catch(() => { eleven.className = 'home-chip todo'; eleven.lastChild.textContent = 'ElevenLabs key not working'; });
  }
  if (!s.settings.aiProvider.endpoint) checks.append(chip(false, 'AI auto-tagging not set up (optional)', 'Global Settings → General & tools → AI auto-tagging', settings));

  const left = h('section.home-hero',
    h('div.home-wave', WAVE.map((x) => h('i', { style: { height: x + '%' } }))),
    h('h1', 'Hello voice artist,', h('br'), 'welcome to the studio.'),
    h('p.home-ask', 'What do you want to do today?'),
    stats, checks);

  // ---- right: what to do
  const cards = h('div.home-cards', CHOICES.map((c, i) => h('button.home-card' + (i === 0 ? '.first' : ''), { style: { '--c': c.color }, onclick: c.go },
    h('span.home-ic', icon(c.icon, { size: 20 })), h('b', c.title), h('small', c.sub))));
  const right = h('section.home-start', h('div.home-lbl', 'Start something'), cards);

  // ---- bottom: work to pick up again, most recently opened first
  const items = recentItems(s, revoices);
  const bottom = h('section.home-recent', h('div.home-lbl', 'Pick up where you left off'),
    items.length ? h('div.home-rcs', items.map((x) => h('button.home-rc', { style: { '--c': x.color }, title: 'Open ' + x.name, onclick: () => go(x.view, { id: x.id }) },
      h('b.ellipsis', x.name), h('small.ellipsis', x.sub),
      h('span.home-bar', h('i', { style: { width: (x.total ? Math.round((100 * x.done) / x.total) : 0) + '%', background: x.total && x.done >= x.total ? 'var(--ok)' : x.color } })),
      h('span.home-ct', x.count))))
      : h('div.faint', 'Nothing yet. Start with one of the options above; your work shows up here.'));

  view.append(h('div.home-top', left, right), bottom);
  return view;
}

function recentItems(s, revoices) {
  const all = [];
  for (const p of s.patches) {
    const n = p.total - p.omitted - (p.excluded || 0);
    all.push({ view: 'patch', id: p.id, name: p.name, sub: `${p.voicePack} · Phase 2${p.kind === 'vc' ? ' · Voice Changer' : ''}`, done: p.generated, total: n, count: `${p.generated} / ${n} lines`, color: p.voiceColor || '#58a6ff' });
  }
  for (const m of s.mods) {
    const lines = m.plugins.reduce((a, x) => a + x.topicLines, 0);
    const unrev = m.plugins.reduce((a, x) => a + x.unreviewed, 0);
    all.push({ view: 'mod', id: m.id, name: m.name, sub: `Phase 1 · ${m.plugins.length} plugin(s)`, done: lines - unrev, total: lines, count: unrev ? `${unrev} flagged to review` : `${lines} lines · reviewed`, color: '#58a6ff' });
  }
  for (const f of s.fomods) {
    all.push({ view: 'fomod', id: f.id, name: f.name, sub: `FOMOD · ${f.lastBuildUtc ? 'built ' + new Date(f.lastBuildUtc).toLocaleDateString() : 'not built yet'}`, done: f.lastBuildUtc ? 1 : 0, total: 1, count: `${f.options} options · ${f.steps} pages`, color: '#3fb950' });
  }
  for (const r of revoices) {
    all.push({ view: 'revoice', id: r.id, name: r.name, sub: `NPC Revoice · ${r.speakers} speakers`, done: r.generated, total: r.lines, count: `${r.generated} / ${r.lines} lines`, color: '#d2a8ff' });
  }
  // Most recently opened first; then the rest in the order above (patches before mods), four in all.
  const order = store.get(RECENT_KEY, []);
  const rank = (x) => { const i = order.findIndex((r) => r.view === x.view && r.id === x.id); return i < 0 ? 1000 : i; };
  return all.map((x, i) => ({ x, i })).sort((a, b) => rank(a.x) - rank(b.x) || a.i - b.i).slice(0, 4).map((y) => y.x);
}
