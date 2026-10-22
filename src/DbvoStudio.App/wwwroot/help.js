// The user guide inside the app: a Help page (contents, search, the whole guide) and a side panel that the
// small ? buttons open at one section. The guide is help/guide.html, exported from the DBVO Studio user guide.
import { rpc, h, clear, icon, debounce } from './lib.js';
import { go } from './app.js';

let guide = null; // a <div> holding the parsed guide

async function load() {
  if (guide) return guide;
  const res = await fetch('help/guide.html');
  if (!res.ok) throw new Error('The user guide is missing from this build (help/guide.html).');
  guide = h('div');
  guide.innerHTML = await res.text();
  return guide;
}

const LEVEL = { H2: 2, H3: 3, H4: 4 };
const INLINE = /^(A|B|I|EM|STRONG|CODE|SPAN|SMALL|SUB|SUP|U|S|MARK|KBD|BR)$/;
/** Text with a space between block elements (list items, paragraphs, cells), so search snippets don't run words together. */
function textOf(n) {
  if (n.nodeType === 3) return n.nodeValue;
  if (n.nodeType !== 1) return '';
  const inner = [...n.childNodes].map(textOf).join('');
  return INLINE.test(n.tagName) ? inner : ' ' + inner + ' ';
}
const headings = (root) => [...root.querySelectorAll('h2[id],h3[id],h4[id]')];

/** The heading with this id and everything under it, up to the next heading of the same or a higher level. */
function sectionNodes(root, id) {
  const head = root.querySelector(`[id="${CSS.escape(id)}"]`);
  if (!head || !LEVEL[head.tagName]) return null;
  const nodes = [head];
  for (let n = head.nextElementSibling; n; n = n.nextElementSibling) {
    if (LEVEL[n.tagName] && LEVEL[n.tagName] <= LEVEL[head.tagName]) break;
    nodes.push(n);
  }
  return nodes;
}

/** Guide links: in-guide anchors stay in the guide; web links open in the browser. */
function wireLinks(el, onAnchor) {
  el.addEventListener('click', (e) => {
    const a = e.target.closest('a[href]');
    if (!a) return;
    const href = a.getAttribute('href');
    e.preventDefault();
    if (href.startsWith('#')) onAnchor(href.slice(1));
    else if (/^https?:/i.test(href)) rpc('open.url', { url: href });
  });
}

// ------------------------------------------------------------------ side panel

let drawer = null;
function closeDrawer() { if (drawer) { drawer.remove(); drawer = null; } }

export async function openHelp(anchor) {
  let root;
  try { root = await load(); } catch (e) { alertMissing(e); return; }
  const nodes = sectionNodes(root, anchor) || sectionNodes(root, 'introduction');
  closeDrawer();
  const body = h('div.help-body.guide');
  body.append(...nodes.map((n) => n.cloneNode(true)));
  const title = nodes[0].textContent;
  nodes.length && body.firstChild.remove(); // the title is shown in the panel's head
  wireLinks(body, (id) => openHelp(id));
  drawer = h('aside.help-drawer', { role: 'dialog', 'aria-label': 'Help: ' + title },
    h('div.help-head', icon('book', { size: 16 }), h('b.grow.ellipsis', title),
      h('button.btn.sm', { title: 'Open the whole guide at this section', onclick: () => { closeDrawer(); document.getElementById('modal').classList.add('hidden'); go('help', { anchor }); } }, 'Open in the guide'),
      h('button.icon-btn', { title: 'Close (Esc)', onclick: closeDrawer }, icon('x', { size: 16 }))),
    body);
  document.body.append(drawer);
}

function alertMissing(e) {
  closeDrawer();
  drawer = h('aside.help-drawer', h('div.help-head', h('b.grow', 'Help'), h('button.icon-btn', { onclick: closeDrawer }, icon('x', { size: 16 }))), h('div.help-body', String(e.message || e)));
  document.body.append(drawer);
}

document.addEventListener('open-help', (e) => openHelp(e.detail.anchor));
// Esc closes the panel first (and only the panel, not a dialog under it).
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && drawer) { e.stopImmediatePropagation(); e.preventDefault(); closeDrawer(); }
}, true);
document.addEventListener('mousedown', (e) => {
  if (drawer && !drawer.contains(e.target) && !e.target.closest('.help-btn')) closeDrawer();
});

// ------------------------------------------------------------------ Help page

export async function helpView(anchor) {
  closeDrawer();
  const view = h('div.view.help-page');
  const root = await load().catch((e) => { view.append(h('div.empty', h('h2', 'User guide not found'), h('div', e.message))); return null; });
  if (!root) return view;
  const content = h('article.guide.help-content');
  content.append(...[...root.childNodes].map((n) => n.cloneNode(true)));
  const heads = headings(content);
  const q = h('input', { type: 'search', placeholder: 'Search the guide…', spellcheck: false });
  const toc = h('nav.help-toc');
  const results = h('div.help-results.hidden');

  const jump = (id) => {
    const el = content.querySelector(`[id="${CSS.escape(id)}"]`);
    if (!el) return;
    el.scrollIntoView({ block: 'start' });
    el.classList.remove('help-flash'); void el.offsetWidth; el.classList.add('help-flash');
    toc.querySelectorAll('a').forEach((a) => a.classList.toggle('on', a.dataset.id === id));
  };
  wireLinks(content, jump);

  for (const hd of heads) {
    if (hd.tagName === 'H4') continue;
    toc.append(h('a.lv' + LEVEL[hd.tagName], { href: '#', 'data-id': hd.id, onclick: (e) => { e.preventDefault(); jump(hd.id); } }, hd.textContent));
  }

  // Search: sections whose text has every word typed, with the first match shown.
  const sectionText = heads.map((hd) => ({ hd, text: (sectionNodes(content, hd.id) || []).map(textOf).join(' ').replace(/\s+/g, ' ').trim() }));
  const search = () => {
    const words = q.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    clear(results);
    toc.classList.toggle('hidden', words.length > 0);
    results.classList.toggle('hidden', !words.length);
    if (!words.length) return;
    // the deepest section that matches wins, so a hit isn't listed again for each parent heading
    const hits = sectionText.filter((s) => words.every((w) => s.text.toLowerCase().includes(w)))
      .filter((s, i, arr) => !arr.some((o) => o !== s && LEVEL[o.hd.tagName] > LEVEL[s.hd.tagName] && s.text.includes(o.text)));
    if (!hits.length) { results.append(h('div.faint', 'Nothing found.')); return; }
    for (const s of hits.slice(0, 40)) {
      const i = s.text.toLowerCase().indexOf(words[0]);
      const from = Math.max(0, i - 50);
      const snip = (from ? '…' : '') + s.text.slice(from, i + 90) + '…';
      results.append(h('a', { href: '#', onclick: (e) => { e.preventDefault(); jump(s.hd.id); } }, h('b', s.hd.textContent), h('small', snip)));
    }
  };
  q.addEventListener('input', debounce(search, 120));

  view.append(
    h('div.view-head', h('h1', 'User Guide'), h('span.sub', 'Every screen and button, explained. The ? buttons in the app open the matching section.'), h('div.spacer'),
      h('button.btn.sm.ghost', { title: 'Back to the landing page', onclick: () => go('home') }, '← Home')),
    h('div.help-split', h('div.help-side', h('div.help-search', icon('search', { size: 15 }), q), results, toc), content));
  requestAnimationFrame(() => { if (anchor) jump(anchor); });
  return view;
}

/** Where F1 opens the guide on each page. */
export const PAGE_HELP = {
  home: 'introduction', mods: 'add-mods', mod: 'review-the-lines', patches: 'the-patch-list', patch: 'the-patch-page', vc: 'voice-changer',
  fomods: '1-start-a-project', fomod: '3-edit-pages-groups-and-options', revoices: 'npc-revoice-tool', revoice: 'setup', wavlip: 'wav-to-lip-tool',
  settings: 'getting-started', voices: '5-create-a-voice-pack', help: 'introduction',
};
