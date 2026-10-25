// The landing page: a welcome, what to do next, the state of the studio and the work to pick up again.
import { rpc, h, store, icon } from './lib.js';
import { app, go, refreshState } from './app.js';

const CHOICES = [
  { icon: 'wave', color: '#58a6ff', title: 'I want to make DBVO patches.', sub: 'Import a mod\'s plugins, review the player lines, then voice them.', go: () => go('mods') },
  { icon: 'mic', color: '#d2a8ff', title: 'I want to revoice an NPC or a whole mod.', sub: 'Give characters in any mod, or the base game, a new voice.', go: () => go('revoices') },
  { icon: 'box', color: '#3fb950', title: 'I want to build a FOMOD installer.', sub: 'Gather your patches into an installer with the DBVO JSON.', go: () => go('fomods') },
  { icon: 'book', color: '#e3b341', title: 'I want to narrate Skyrim\'s books.', sub: 'Patch the base game or mods for Voiced Books of Skyrim.', go: () => go('books') },
  { icon: 'lips', color: '#f0883e', title: 'I want lip sync for WAV files I already have.', sub: 'Whisper listens, FaceFXWrapper writes the .lip files.', go: () => go('wavlip') },
  { icon: 'gear', color: '#8b949e', title: 'I want to set up the studio.', sub: 'Library, tools, ElevenLabs key and voice packs.', go: () => go('settings', { tab: 'general' }) },
];

// The welcome: the first one on the very first launch, then the next one each time DBVO Studio opens, looping.
const GREETINGS = [
  ['Hello voice artist!', 'Let’s give the Dragonborn a voice.'],
  ['Hello voice director!', 'Welcome back to the studio.'],
  ['Welcome, voice producer!', 'Let’s give Skyrim something new to say.'],
  ['Good to see you, voice creator!', 'The studio is all yours.'],
  ['Welcome back to the studio!', 'Your audience in Skyrim is waiting.'],
  ['Hello voice producer!', 'Let’s make some noise in Nirn.'],
];
let greeting = null; // picked once per launch
function pickGreeting() {
  if (greeting) return greeting;
  const last = Number(store.get('greeting', -1));
  const i = Number.isInteger(last) && last >= 0 ? (last + 1) % GREETINGS.length : 0;
  store.set('greeting', i);
  return (greeting = GREETINGS[i]);
}

// Under the welcome: a word from M'aiq the Liar (UESP). The first launch shows the snowflakes; then the rest in a shuffled
// order, each once, before the snowflakes come round again.
const MAIQ = [
  'Snow falls. Why worry where it goes? M\'aiq thinks the snowflakes are pretty.',
  'M\'aiq knows much, and tells some. M\'aiq knows many things others do not.',
  'M\'aiq knows many things, no?',
  'It does not matter to M\'aiq how strong or smart one is. It only matters what one can do.',
  'Much snow in Skyrim. Enough snow. M\'aiq does not want any more.',
  'M\'aiq once walked to High Hrothgar. So many steps, he lost count.',
  'Too much magic can be dangerous. M\'aiq once had two spells and burned his sweetroll.',
  'What does this mean, to combine magic? Magic plus magic is still magic.',
  'Dragons were never gone. They were just invisible and very, very quiet.',
  'Some say Alduin is Akatosh. Some say M\'aiq is a Liar. Don\'t you believe either of those things.',
  'M\'aiq hears many stories of war... yet few of them are true.',
  'How does one know there was a city of Winterhold? M\'aiq did not see it with his eyes. Did you?',
  'M\'aiq knows why Falmer are blind. It has nothing to do with the Dwemer disappearing. Really.',
  'M\'aiq is always in search of calipers, yet finds none. Where could they have gone?',
  'M\'aiq is very practical. He has no need for mysticism.',
  'M\'aiq\'s father was also called M\'aiq. As was M\'aiq\'s father\'s father. At least, that\'s what his father said.',
  'Nords are so serious about beards. So many beards. M\'aiq thinks they wish they had glorious manes like Khajiit.',
  'Nords\' armor has lots of fur. This sometimes makes M\'aiq nervous.',
  'M\'aiq can travel fast across the land. Some lazy types take carriages. It is all the same to M\'aiq.',
  'M\'aiq carries two weapons, to be safe. What if one breaks? That would be most unlucky.',
  'Don\'t try blocking if you have two weapons. You will only get confused. Much better to hit twice anyway.',
  'Some like taking friends on adventures. M\'aiq thinks being alone is better. Less arguing about splitting treasure.',
  'Once M\'aiq got in trouble in Riften, and fled to Windhelm. It is good that nobody there cared.',
  'M\'aiq saw a mudcrab the other day. Horrible creatures.',
  'Werebears? Where? Bears? Men that are bears?',
  'Skyrim was once the land of many butterflies. Now, not so much.',
];
let maiq = null; // picked once per launch
function pickMaiq() {
  if (maiq != null) return maiq;
  let deck = store.get('maiqDeck', null);
  let i;
  if (!Array.isArray(deck) || !deck.length || deck.some((x) => !Number.isInteger(x) || x < 1 || x >= MAIQ.length)) {
    i = 0; // the first launch, and the start of every round
    deck = [...Array(MAIQ.length - 1).keys()].map((x) => x + 1);
    for (let k = deck.length - 1; k > 0; k--) { const j = Math.floor(Math.random() * (k + 1)); [deck[k], deck[j]] = [deck[j], deck[k]]; }
  } else i = deck.shift();
  store.set('maiqDeck', deck);
  return (maiq = MAIQ[i]);
}

const WAVE = [18, 30, 52, 40, 70, 88, 60, 94, 72, 46, 80, 100, 64, 38, 58, 84, 50, 28, 44, 22, 36, 16];
const RECENT_KEY = 'recent';
const RECENT_VIEWS = ['mod', 'patch', 'fomod', 'revoice', 'book', 'grunt'];

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
  const books = await rpc('books.list').catch(() => []);
  const grunts = await rpc('grunts.list').catch(() => []);
  const view = h('div.view.home.reading');

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
  checks.append(chip(!missing.length, missing.length ? `${missing.length} of 5 tools missing` : 'All 5 tools found', missing.length ? 'Missing: ' + missing.join(', ') : 'FonixData, xWMAEncode, ffmpeg, FaceFXWrapper and 7-Zip are all set (Global Settings → General & tools)', settings));
  if (!s.voicePacks.length) checks.append(chip(false, 'No voice pack yet', 'Create one in Global Settings → Voice packs', () => go('settings', { tab: 'voices', id: 'new' })));
  const eleven = chip(s.settings.hasApiKey, s.settings.hasApiKey ? 'ElevenLabs key saved' : 'ElevenLabs key not set', 'Your ElevenLabs API key (Global Settings → General & tools)', settings);
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
    h('h1', pickGreeting()[0], h('br'), pickGreeting()[1]),
    h('figure.home-quote', h('blockquote', '“' + pickMaiq().replace(/'/g, '’') + '”'), h('figcaption', '— M’aiq the Liar')),
    stats, checks);

  // ---- right: what to do
  const cards = h('div.home-cards', CHOICES.map((c, i) => h('button.home-card' + (i === 0 ? '.first' : ''), { title: c.sub, style: { '--c': c.color }, onclick: c.go },
    h('span.home-ic', icon(c.icon, { size: 20 })), h('b', c.title), h('small', c.sub))));
  const right = h('section.home-start', h('div.home-lbl', 'Start something'), cards);

  // ---- bottom: work to pick up again, most recently opened first
  const items = recentItems(s, revoices, books, grunts);
  const bottom = h('section.home-recent', h('div.home-lbl', 'Pick up where you left off'),
    items.length ? h('div.home-rcs', items.map((x) => h('button.home-rc', { style: { '--c': x.color }, title: 'Open ' + x.name, onclick: () => go(x.view, { id: x.id }) },
      h('span.rc-top', h('b.ellipsis', x.name), h('span.home-ct', x.count)),
      h('span.rc-bot', h('small.ellipsis', x.sub),
        h('span.home-bar', h('i', { style: { width: (x.total ? Math.round((100 * x.done) / x.total) : 0) + '%', background: x.total && x.done >= x.total ? 'var(--ok)' : x.color } }))))))
      : h('div.faint', 'Nothing yet. Start with one of the options above; your work shows up here.'));

  view.append(h('div.home-top', left, right), bottom);
  return view;
}

function recentItems(s, revoices, books = [], grunts = []) {
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
  for (const b of books) {
    all.push({ view: 'book', id: b.id, name: b.name, sub: `Voiced Books · ${b.books} books`, done: b.generated, total: b.lines, count: `${b.generated} / ${b.lines} parts`, color: '#e3b341' });
  }
  for (const g of grunts) {
    all.push({ view: 'grunt', id: g.id, name: g.name, sub: `Player Grunts · ${g.target === 'spvs' ? 'SPVS ' + g.slot : g.multi ? 'MPCHT' : 'PCHT'}`, done: g.events, total: g.total, count: `${g.events} / ${g.total} events`, color: '#f778ba' });
  }
  // Most recently opened first; then the rest in the order above (patches before mods), eight in all (two rows).
  const order = store.get(RECENT_KEY, []);
  const rank = (x) => { const i = order.findIndex((r) => r.view === x.view && r.id === x.id); return i < 0 ? 1000 : i; };
  return all.map((x, i) => ({ x, i })).sort((a, b) => rank(a.x) - rank(b.x) || a.i - b.i).slice(0, 8).map((y) => y.x);
}
