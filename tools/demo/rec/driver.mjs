// Presentation helpers: a visible mouse pointer, click ripples, captions, a file-drag ghost, frame capture.
import fs from 'fs';

const OVERLAY_CSS = `
#__cur{display:none!important;position:fixed;left:0;top:0;width:22px;height:22px;z-index:2147483647;pointer-events:none;transform:translate(-100px,-100px);transition:none}
#__cur svg{filter:drop-shadow(0 1px 1.5px rgba(0,0,0,.55))}
.__ripple{display:none!important;position:fixed;width:34px;height:34px;margin:-17px 0 0 -17px;border-radius:50%;border:2px solid rgba(255,255,255,.85);z-index:2147483646;pointer-events:none;animation:__rp .45s ease-out forwards}
@keyframes __rp{from{transform:scale(.35);opacity:.9}to{transform:scale(1.25);opacity:0}}
#__cap{position:fixed;left:50%;bottom:38px;transform:translate(-50%,16px);z-index:2147483645;pointer-events:none;opacity:0;transition:opacity .35s ease,transform .35s ease;
  background:rgba(13,17,23,.92);border:1px solid rgba(88,166,255,.45);border-radius:12px;padding:12px 22px 13px;box-shadow:0 10px 32px rgba(0,0,0,.55);max-width:980px;text-align:center;font-family:"Segoe UI",Selawik,sans-serif}
#__cap.on{opacity:1;transform:translate(-50%,0)}
#__cap .k{display:block;font-size:12px;letter-spacing:1.4px;text-transform:uppercase;color:#58a6ff;font-weight:700;margin-bottom:3px}
#__cap .t{font-size:19px;color:#e6edf3;font-weight:600;line-height:1.35}
#__ghost{visibility:hidden!important;position:fixed;z-index:2147483644;pointer-events:none;display:none;align-items:center;gap:8px;padding:7px 12px 7px 9px;border-radius:7px;background:rgba(32,96,190,.88);color:#fff;font:600 13px "Segoe UI",Selawik,sans-serif;box-shadow:0 6px 18px rgba(0,0,0,.45)}
#__ghost .n{background:#fff;color:#1f6feb;border-radius:9px;padding:0 7px;font-size:12px}
#__fade{position:fixed;inset:0;background:#05070a;z-index:2147483642;pointer-events:none;opacity:0;transition:opacity .35s}
#__spot{position:fixed;z-index:2147483643;pointer-events:none;border:2px solid #f0b72f;border-radius:9px;box-shadow:0 0 0 9999px rgba(0,0,0,.28);opacity:0;transition:opacity .3s}
`;
const CURSOR = `<svg width="22" height="22" viewBox="0 0 22 22"><path d="M3 2 L3 18.5 L7.2 14.6 L10 20.6 L12.8 19.4 L10.1 13.5 L15.8 13.5 Z" fill="#fff" stroke="#000" stroke-width="1.2" stroke-linejoin="round"/></svg>`;

export async function overlay(page) {
  await page.addStyleTag({ content: OVERLAY_CSS });
  await page.evaluate((cursor) => {
    if (document.getElementById('__cur')) return;
    const c = document.createElement('div'); c.id = '__cur'; c.innerHTML = cursor; document.body.append(c);
    const cap = document.createElement('div'); cap.id = '__cap'; cap.innerHTML = '<span class="k"></span><span class="t"></span>'; document.body.append(cap);
    const g = document.createElement('div'); g.id = '__ghost'; document.body.append(g);
    const s = document.createElement('div'); s.id = '__spot'; document.body.append(s);
    const fd = document.createElement('div'); fd.id = '__fade'; document.body.append(fd);
    const move = (x, y) => { c.style.transform = `translate(${x - 3}px,${y - 2}px)`; g.style.left = x + 18 + 'px'; g.style.top = y + 14 + 'px'; };
    window.__moveCursor = move;
    document.addEventListener('mousemove', (e) => move(e.clientX, e.clientY), true);
    document.addEventListener('dragover', (e) => move(e.clientX, e.clientY), true);
    document.addEventListener('mousedown', (e) => { const r = document.createElement('div'); r.className = '__ripple'; r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px'; document.body.append(r); setTimeout(() => r.remove(), 500); }, true);
  }, CURSOR);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export { sleep };

export const track = [];
const now = () => performance.now() / 1000;
export function makeDriver(page) {
  const mv = (x, y) => { track.push({ t: now(), x, y }); return page.mouse.move(x, y); };
  let pos = { x: 768, y: 480 };
  const ease = (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
  async function center(target) {
    if (target && typeof target.x === 'number') return target;
    const loc = typeof target === 'string' ? page.locator(target).first() : target;
    let b = null;
    for (let i = 0; i < 20 && !b; i++) {
      await loc.waitFor({ state: 'visible', timeout: 2500 });
      await loc.scrollIntoViewIfNeeded().catch(() => {});
      b = await loc.boundingBox().catch(() => null);
      if (!b) await sleep(60);
    }
    if (!b) throw new Error('no box for ' + loc);
    return { x: b.x + b.width / 2 + (Math.random() - 0.5) * Math.min(10, b.width / 4), y: b.y + b.height / 2 + (Math.random() - 0.5) * Math.min(4, b.height / 4) };
  }
  const d = {
    page,
    async move(target, ms) {
      const to = await center(target);
      const dist = Math.hypot(to.x - pos.x, to.y - pos.y);
      const dur = ms ?? Math.min(900, 260 + dist * 0.45);
      const steps = Math.max(8, Math.round(dur / 16));
      const from = { ...pos };
      // a slight arc, like a hand
      const bend = (Math.random() - 0.5) * Math.min(80, dist * 0.12);
      for (let i = 1; i <= steps; i++) {
        const t = ease(i / steps);
        const nx = -(to.y - from.y) / (dist || 1), ny = (to.x - from.x) / (dist || 1);
        const arc = Math.sin(Math.PI * t) * bend;
        await mv(from.x + (to.x - from.x) * t + nx * arc, from.y + (to.y - from.y) * t + ny * arc);
        await sleep(dur / steps);
      }
      pos = to;
      return to;
    },
    async click(target, { pause = 380, before = 120 } = {}) {
      await d.move(target);
      await sleep(before);
      track.push({ t: now(), click: 1, x: pos.x, y: pos.y });
      await page.mouse.down(); await sleep(70); await page.mouse.up();
      await sleep(pause);
    },
    async hover(target, ms = 600) { await d.move(target); await sleep(ms); },
    async type(text, { delay = 38 } = {}) {
      for (const ch of text) { await page.keyboard.type(ch); await sleep(delay + Math.random() * delay * 0.8); }
    },
    async clickType(target, text, opts = {}) {
      await d.click(target, { pause: 150 });
      if (opts.clear) { await page.keyboard.press('Control+A'); await sleep(80); }
      await d.type(text, opts);
      await sleep(opts.pause ?? 250);
    },
    async caption(kicker, text, ms = 0) {
      track.push({ t: now(), cap: [kicker, text] });
      await page.evaluate(([k, t]) => { const c = document.getElementById('__cap'); c.querySelector('.k').textContent = k; c.querySelector('.t').textContent = t; c.classList.add('on'); }, [kicker, text]);
      if (ms) await sleep(ms);
    },
    async uncaption() { track.push({ t: now(), cap: null }); await page.evaluate(() => document.getElementById('__cap').classList.remove('on')); await sleep(250); },
    async spot(target, ms = 1400, pad = 8) {
      const loc = typeof target === 'string' ? page.locator(target).first() : target;
      const b = await loc.boundingBox();
      await page.evaluate(([b, pad]) => { const s = document.getElementById('__spot'); Object.assign(s.style, { left: b.x - pad + 'px', top: b.y - pad + 'px', width: b.width + pad * 2 + 'px', height: b.height + pad * 2 + 'px', opacity: 1 }); }, [b, pad]);
      await sleep(ms);
      await page.evaluate(() => { document.getElementById('__spot').style.opacity = 0; });
      await sleep(250);
    },
    // Drag files in from outside the window (Explorer) and drop them on a target.
    async dropFiles(target, names, { label } = {}) {
      const to = await center(target);
      track.push({ t: now(), ghost: `${label || names[0]}|${names.length}` });
      await page.evaluate(([label, n]) => { const g = document.getElementById('__ghost'); g.innerHTML = `<svg width="16" height="18" viewBox="0 0 16 18"><path d="M2 1h8l4 4v12H2z" fill="#fff" opacity=".95"/><path d="M10 1v4h4" fill="none" stroke="#1f6feb"/></svg>${label}<span class="n">${n}</span>`; g.style.display = 'flex'; }, [label || names[0], names.length]);
      pos = { x: 1520, y: Math.max(160, to.y - 100) };
      await mv(pos.x, pos.y);
      // dragenter / dragover while moving
      const dt = await page.evaluateHandle((names) => { const dt = new DataTransfer(); for (const n of names) dt.items.add(new File(['x'], n)); return dt; }, names);
      const steps = 40;
      const from = { ...pos };
      for (let i = 1; i <= steps; i++) {
        const t = ease(i / steps);
        const x = from.x + (to.x - from.x) * t, y = from.y + (to.y - from.y) * t;
        await mv(x, y);
        await page.evaluate(([x, y, dt]) => { const el = document.elementFromPoint(x, y); if (el) { el.dispatchEvent(new DragEvent('dragenter', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: dt })); el.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: dt })); } }, [x, y, dt]);
        await sleep(18);
      }
      pos = to;
      await sleep(250);
      track.push({ t: now(), ghost: null });
      await page.evaluate(([x, y, dt]) => { document.getElementById('__ghost').style.display = 'none'; const el = document.elementFromPoint(x, y); el.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: dt })); }, [to.x, to.y, dt]);
      await sleep(400);
    },
    async scrollWheel(target, dy, steps = 6) { await d.move(target); for (let i = 0; i < steps; i++) { await page.mouse.wheel(0, dy / steps); await sleep(40); } },
    // Native <select> popups are not painted in a headless screencast: draw one like Chromium's dark popup.
    async select(target, value, { pause = 350 } = {}) {
      const loc = typeof target === 'string' ? page.locator(target).first() : target;
      const at = await d.move(loc);
      await sleep(110);
      await d.ripple(at);
      await loc.focus().catch(() => {});
      await sleep(120);
      const b = await loc.boundingBox();
      const opts = await loc.evaluate((el) => [...el.options].map((o) => ({ v: o.value, t: o.textContent, sel: o.selected })));
      await page.evaluate(([b, opts]) => {
        const m = document.createElement('div'); m.id = '__sel';
        Object.assign(m.style, { position: 'fixed', left: b.x + 'px', top: b.y + b.height + 2 + 'px', minWidth: b.width + 'px', zIndex: 2147483640, background: '#2b2b2b', border: '1px solid #555', borderRadius: '4px', padding: '4px 0', boxShadow: '0 8px 22px rgba(0,0,0,.5)', font: '14px "Segoe UI",Selawik,sans-serif', color: '#fff', maxHeight: '320px', overflow: 'hidden' });
        for (const o of opts) { const r = document.createElement('div'); r.textContent = o.t; r.dataset.v = o.v; Object.assign(r.style, { padding: '4px 12px', background: o.sel ? '#3d6fb5' : 'transparent', whiteSpace: 'nowrap' }); m.append(r); }
        document.body.append(m);
      }, [b, opts]);
      await sleep(350);
      const row = page.locator(`#__sel > div[data-v="${value.replace(/"/g, '\"')}"]`);
      const rb = await row.boundingBox();
      await page.evaluate((v) => { for (const r of document.querySelectorAll('#__sel > div')) r.addEventListener('mouseenter', () => { for (const x of document.querySelectorAll('#__sel > div')) x.style.background = 'transparent'; r.style.background = '#3d6fb5'; }); }, value);
      await d.move({ x: rb.x + Math.min(60, rb.width / 2), y: rb.y + rb.height / 2 }, 420);
      await page.evaluate((v) => { for (const x of document.querySelectorAll('#__sel > div')) x.style.background = x.dataset.v === v ? '#3d6fb5' : 'transparent'; }, value);
      await sleep(180);
      await d.ripple({ x: rb.x + Math.min(60, rb.width / 2), y: rb.y + rb.height / 2 });
      await sleep(60);
      await page.evaluate(() => document.getElementById('__sel')?.remove());
      await loc.selectOption(value);
      await sleep(pause);
    },
    async fade(on) { await page.evaluate((on) => { document.getElementById('__fade').style.opacity = on ? 1 : 0; }, on); await sleep(420); },
    async ripple(at) { track.push({ t: now(), click: 1, x: at.x, y: at.y }); await page.evaluate(({ x, y }) => { const r = document.createElement('div'); r.className = '__ripple'; r.style.left = x + 'px'; r.style.top = y + 'px'; document.body.append(r); setTimeout(() => r.remove(), 500); }, at); },
    wait: sleep,
  };
  return d;
}

// CDP screencast: every painted frame with its timestamp, assembled later at a constant frame rate.
export async function startCapture(page, dir) {
  // Full-resolution screenshots in a loop (the screencast only delivers CSS-pixel frames).
  fs.mkdirSync(dir, { recursive: true });
  const cdp = await page.context().newCDPSession(page);
  const frames = [];
  let n = 0, paused = false, cut = false, running = true;
  const loop = (async () => {
    while (running) {
      const t0 = performance.now();
      if (!paused) {
        try {
          const r = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 92, optimizeForSpeed: true, captureBeyondViewport: false, clip: { x: 0, y: 0, width: 1536, height: 832, scale: 1.25 } });
          const file = `${dir}/f${String(n++).padStart(6, '0')}.jpg`;
          fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
          frames.push({ file, t: t0 / 1000, cutBefore: cut });
          cut = false;
        } catch { /* page busy */ }
      }
      const dt = performance.now() - t0;
      if (dt < 20) await sleep(20 - dt);
    }
  })();
  return {
    pause() { paused = true; },
    resume() { paused = false; cut = true; },
    async stop() {
      running = false;
      await loop;
      const lines = [];
      for (let i = 0; i < frames.length; i++) {
        let dur = i + 1 < frames.length ? Math.max(0.001, frames[i + 1].t - frames[i].t) : 0.8;
        if (i + 1 < frames.length && frames[i + 1].cutBefore) dur = 0.04;
        lines.push(`file '${frames[i].file}'`, `duration ${dur.toFixed(4)}`);
      }
      if (frames.length) lines.push(`file '${frames[frames.length - 1].file}'`);
      fs.writeFileSync(`${dir}/list.txt`, lines.join('\n'));
      fs.writeFileSync(`${dir}/frames.json`, JSON.stringify(frames.map((f) => ({ file: f.file, t: f.t, cut: f.cutBefore }))));
      fs.writeFileSync(`${dir}/track.json`, JSON.stringify(track));
      return frames.length;
    },
  };
}
