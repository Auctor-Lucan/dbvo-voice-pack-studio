// Runs the real DBVO Studio backend (demo host) behind the real web UI in Chromium, with Windows-style paths.
import fs0 from 'fs';
import { createRequire } from 'module';
// Playwright: the session's preinstalled copy (require('playwright') works from any folder there), else the one at /opt/node-tools.
const { chromium } = (() => { try { return createRequire(import.meta.url)('playwright'); } catch { return createRequire('/opt/node-tools/node_modules/')('playwright'); } })();
import { spawn } from 'child_process';
import fs from 'fs';
import path from 'path';
import readline from 'readline';

export const DEMO = process.env.DEMO || '/tmp/dbvo-work/demo';
export const WWW = process.env.WWW || '/home/claude/dbvo-voice-pack-studio/src/DbvoStudio.App/wwwroot';
export const LIB = '/srv/dbvo/D/DBVO Studio Library';

// /srv/dbvo/C/a/b  <->  C:\a\b   (inside JSON text, where a backslash is written as \\)
export function toWin(json) {
  return json.replace(/\/srv\/dbvo\/([A-Z])((?:\/(?:[^"\\/]|\\.)*)*)/g, (m, d, rest) => `${d}:${rest.replace(/\//g, '\\\\') || '\\\\'}`);
}
export function toLinux(json) {
  return json.replace(/\b([A-Z]):((?:\\\\(?:[^"\\]|\\[^\\])*)+)/g, (m, d, rest) => `/srv/dbvo/${d}${rest.replace(/\\\\/g, '/')}`);
}

export async function start({ width = 1536, height = 832, scale = 1.25, record = null } = {}) {
  const api = spawn('python3', [DEMO + '/svc/fake_api.py'], { stdio: 'inherit' });
  let host, page;
  const spawnHost = () => {
    host = spawn('/opt/dotnet/dotnet', [DEMO + '/host/bin/Debug/net10.0/DemoHost.dll'], {
      env: { ...process.env, HOME: DEMO + '/home', DBVO_ELEVEN: 'http://127.0.0.1:8931/', DOTNET_CLI_HOME: DEMO + '/home' },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    host.stderr.on('data', (d) => { const s = d.toString(); if (!/^\[log\] Whisper/.test(s)) process.stderr.write('HOST ' + s); });
    const rl = readline.createInterface({ input: host.stdout });
    rl.on('line', (l) => { page && page.evaluate((s) => window.__deliver && window.__deliver(s), toWin(l)).catch(() => {}); });
  };
  spawnHost();
  const browser = await chromium.launch({ ...(fs0.existsSync('/opt/pw-browsers/chromium-1194/chrome-linux/chrome') ? { executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' } : {}), args: ['--font-render-hinting=none', '--disable-lcd-text', '--disable-background-networking', '--disable-component-update', '--no-pings', '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] });
  const ctx = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: scale, recordVideo: record ? { dir: record, size: { width, height } } : undefined });
  page = await ctx.newPage();
  page.on('pageerror', (e) => console.log('PAGEERROR', e.message));
  page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE', m.text()); });
  let pendingDrop = [];
  await page.exposeBinding('__hostPost', (src, msg, withDrop) => {
    const json = toLinux(msg);
    if (withDrop) { host.stdin.write('DROP ' + JSON.stringify(pendingDrop.map((p) => toLinux(JSON.stringify(p)).slice(1, -1))) + '\n'); pendingDrop = []; }
    host.stdin.write(json + '\n');
  });
  await page.addInitScript(() => {
    const fns = [];
    window.chrome = { webview: {
      addEventListener: (t, fn) => { if (t === 'message') fns.push(fn); },
      postMessage: (m) => window.__hostPost(typeof m === 'string' ? m : JSON.stringify(m), false),
      postMessageWithAdditionalObjects: (m) => window.__hostPost(typeof m === 'string' ? m : JSON.stringify(m), true),
    } };
    document.addEventListener('DOMContentLoaded', () => { document.documentElement.spellcheck = false; });
    window.__deliver = (s) => { const d = JSON.parse(s); for (const f of fns) f({ data: d }); };
  });
  const type = (p) => (p.endsWith('.js') ? 'text/javascript' : p.endsWith('.css') ? 'text/css' : p.endsWith('.html') ? 'text/html' : p.endsWith('.svg') ? 'image/svg+xml' : p.endsWith('.png') ? 'image/png' : p.endsWith('.webp') ? 'image/webp' : p.endsWith('.jpg') ? 'image/jpeg' : p.endsWith('.wav') ? 'audio/wav' : 'application/octet-stream');
  await page.route('https://app.dbvo/**', (r) => { const p = path.join(WWW, decodeURIComponent(new URL(r.request().url()).pathname)); fs.existsSync(p) ? r.fulfill({ status: 200, contentType: type(p), body: fs.readFileSync(p) }) : r.fulfill({ status: 404 }); });
  await page.route('https://media.dbvo/**', (r) => { const p = path.join(LIB, decodeURIComponent(new URL(r.request().url()).pathname)); fs.existsSync(p) ? r.fulfill({ status: 200, contentType: type(p), body: fs.readFileSync(p) }) : r.fulfill({ status: 404 }); });
  await page.route('https://user.dbvo/**', (r) => { const p = path.join(DEMO, 'home/.local/share/DBVOStudio/UserFiles', decodeURIComponent(new URL(r.request().url()).pathname)); fs.existsSync(p) ? r.fulfill({ status: 200, contentType: type(p), body: fs.readFileSync(p) }) : r.fulfill({ status: 404 }); });
  const killAll = () => { try { host.kill('SIGKILL'); } catch {} try { api.kill('SIGKILL'); } catch {} };
  process.on('exit', killAll);
  setTimeout(() => { console.log('WATCHDOG'); killAll(); process.exit(3); }, Number(process.env.WATCHDOG || 100000)).unref();
  return {
    browser, ctx, page, api,
    get host() { return host; },
    async restartHost() { const old = host; old.kill('SIGKILL'); await new Promise((r) => setTimeout(r, 300)); spawnHost(); await new Promise((r) => setTimeout(r, 2500)); },
    pick: (...paths) => host.stdin.write('PICK ' + toLinux(JSON.stringify(paths)) + '\n'),
    dropNext: (paths) => { pendingDrop = paths; },
    async close() { await ctx.close(); await browser.close(); host.kill(); api.kill(); },
  };
}
