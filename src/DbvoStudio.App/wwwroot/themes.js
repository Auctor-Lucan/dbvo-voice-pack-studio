// Colour themes. Each sets the CSS variables in styles.css; everything else derives from them.
export const THEMES = [
  { id: 'github-dark', name: 'GitHub Dark', scheme: 'dark', v: { bg: '#0d1117', panel: '#161b22', raised: '#21262d', line: '#30363d', text: '#e6edf3', dim: '#8b949e', faint: '#6e7681', accent: '#2f81f7', link: '#79c0ff', ok: '#3fb950', warn: '#d29922', bad: '#f85149', deep: '#010409' } },
  { id: 'github-dimmed', name: 'GitHub Dimmed', scheme: 'dark', v: { bg: '#22272e', panel: '#2d333b', raised: '#373e47', line: '#444c56', text: '#adbac7', dim: '#768390', faint: '#636e7b', accent: '#539bf5', link: '#6cb6ff', ok: '#57ab5a', warn: '#c69026', bad: '#e5534b', deep: '#1c2128' } },
  { id: 'github-light', name: 'GitHub Light', scheme: 'light', v: { bg: '#ffffff', panel: '#f6f8fa', raised: '#eaeef2', line: '#d0d7de', text: '#1f2328', dim: '#59636e', faint: '#818b98', accent: '#0969da', link: '#0969da', ok: '#1a7f37', warn: '#9a6700', bad: '#cf222e', deep: '#f6f8fa' } },
  { id: 'tokyo-night', name: 'Tokyo Night', scheme: 'dark', v: { bg: '#1a1b26', panel: '#16161e', raised: '#24283b', line: '#292e42', text: '#c0caf5', dim: '#a9b1d6', faint: '#565f89', accent: '#7aa2f7', link: '#7dcfff', ok: '#9ece6a', warn: '#e0af68', bad: '#f7768e', deep: '#13131a' } },
  { id: 'night-owl', name: 'Night Owl', scheme: 'dark', v: { bg: '#011627', panel: '#01111d', raised: '#0b2942', line: '#1d3b53', text: '#d6deeb', dim: '#8badc1', faint: '#637777', accent: '#82aaff', link: '#7fdbca', ok: '#addb67', warn: '#ecc48d', bad: '#ef5350', deep: '#010e1a' } },
  { id: 'deep-sea', name: 'Deep Sea', scheme: 'dark', onAccent: '#06201b', strong: '#2bbfa3', v: { bg: '#0e1416', panel: '#131b1e', raised: '#1a2428', line: '#263439', text: '#d9e5e7', dim: '#8ea3a8', faint: '#62767b', accent: '#34d1b2', link: '#5fd8c4', ok: '#3ccf8e', warn: '#e3b25e', bad: '#f0726f', deep: '#0b1012' } },
  { id: 'nexus', name: 'Nexus', scheme: 'dark', onAccent: '#ffffff', strong: '#d9822b', v: { bg: '#18181b', panel: '#222226', raised: '#2c2c31', line: '#3b3b41', text: '#ececee', dim: '#a6a6ad', faint: '#74747c', accent: '#e8913a', link: '#ee9b45', ok: '#4caf50', warn: '#e8913a', bad: '#e5484d', deep: '#0f0f11' } },
  { id: 'ayu-mirage', name: 'Ayu Mirage', scheme: 'dark', onAccent: '#1f2430', strong: '#ffcc66', v: { bg: '#1f2430', panel: '#1c212b', raised: '#242936', line: '#33415e', text: '#cccac2', dim: '#8a9199', faint: '#707a8c', accent: '#ffcc66', link: '#73d0ff', ok: '#87d96c', warn: '#ffad66', bad: '#f28779', deep: '#171b24' } },
  { id: 'solarized-dark', name: 'Solarized Dark', scheme: 'dark', v: { bg: '#002b36', panel: '#073642', raised: '#0e4452', line: '#1f5664', text: '#eee8d5', dim: '#93a1a1', faint: '#657b83', accent: '#268bd2', link: '#2aa198', ok: '#859900', warn: '#b58900', bad: '#dc322f', deep: '#00212b' } },
  { id: 'dragonbone', name: 'Dragonbone', scheme: 'dark', v: { bg: '#15130f', panel: '#1d1a15', raised: '#27231c', line: '#3a3328', text: '#e8dfcc', dim: '#a89c86', faint: '#7d725f', accent: '#b8893c', link: '#d9bf86', ok: '#8fb26a', warn: '#d9913b', bad: '#c8553d', deep: '#0e0c09' } },
];

export function applyTheme(id) {
  const t = THEMES.find((x) => x.id === id) || THEMES[0];
  const r = document.documentElement.style;
  const v = t.v;
  const set = (k, x) => r.setProperty('--' + k, x);
  for (const k of ['bg', 'panel', 'raised', 'line', 'text', 'dim', 'faint', 'accent', 'link', 'ok', 'warn', 'bad', 'deep']) set(k, v[k]);
  set('panel-2', `color-mix(in srgb, ${v.panel} 50%, ${v.raised})`);
  set('line-soft', v.raised);
  set('accent-strong', t.strong || (t.scheme === 'dark' ? `color-mix(in srgb, ${v.accent} 85%, #000)` : v.accent));
  set('on-accent', t.onAccent || '#ffffff');
  set('accent-soft', `color-mix(in srgb, ${v.accent} 15%, transparent)`);
  r.setProperty('color-scheme', t.scheme);
  try { localStorage.setItem('dbvo:theme', t.id); } catch { /* preview */ }
  // The Windows title bar follows the theme (Windows 11; Windows 10 gets dark or light only).
  try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ id: 0, method: 'window.theme', params: { caption: v.deep, text: v.text, dark: t.scheme === 'dark' } }); } catch { /* not in the app */ }
  return t;
}

// Apply the last theme before the first render, so there is no flash of the default.
try { const saved = localStorage.getItem('dbvo:theme'); if (saved) applyTheme(saved); } catch { /* no storage */ }
